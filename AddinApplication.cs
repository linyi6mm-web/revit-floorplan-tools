using System;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitAutoAnnotate
{
    public sealed class AddinApplication : IExternalApplication
    {
        internal static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("5FA816A1-43DB-410D-90C0-9FCCE47BE4E1"));
        internal static ToolPane Pane { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            var pane = new ToolPane();
            // A restored dockable pane can be used before its ribbon command is ever clicked.
            pane.EnsureExternalEvent();

            application.CreateRibbonTab("床伏図ツール");
            RibbonPanel panel = application.CreateRibbonPanel("床伏図ツール", "施工図");
            var ribbonButton = (PushButton)panel.AddItem(new PushButtonData("FloorPlanDimensions",
                "自動寸法", Assembly.GetExecutingAssembly().Location, typeof(OpenDimensionPane).FullName));
            ribbonButton.ToolTip = "床伏図の自動寸法パネルを開きます";
            ribbonButton.LargeImage = ToolPane.MakeIcon();
            var boxButton = (PushButton)panel.AddItem(new PushButtonData("LinkedSelectionBox",
                LinkedSelectionBox.CommandName, Assembly.GetExecutingAssembly().Location, typeof(LinkedSelectionBox).FullName));
            boxButton.ToolTip = "リンク内の部材を選び、専用の 3D ビューに切断ボックスを設定します";
            boxButton.LargeImage = LinkedSelectionBox.MakeIcon();
            application.RegisterDockablePane(PaneId, "床伏図の自動寸法", pane);
            Pane = pane;
            application.ViewActivated += OnViewActivated;
            return Result.Succeeded;
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e) => Pane?.ShowContext(e.CurrentActiveView);

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ViewActivated -= OnViewActivated;
            Pane?.Close();
            Pane = null;
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class OpenDimensionPane : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (data.Application.ActiveUIDocument == null)
            {
                TaskDialog.Show("床伏図ツール", "Revit プロジェクトを開き、対象の床伏図を表示してください。");
                return Result.Cancelled;
            }
            AddinApplication.Pane.EnsureExternalEvent();
            AddinApplication.Pane.ShowContext(data.Application.ActiveUIDocument.ActiveView);
            data.Application.GetDockablePane(AddinApplication.PaneId).Show();
            return Result.Succeeded;
        }
    }
}
