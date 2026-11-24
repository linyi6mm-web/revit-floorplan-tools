using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using View = Autodesk.Revit.DB.View;

namespace RevitAutoAnnotate
{
    public partial class ToolPane : Page, IDockablePaneProvider, IExternalEventHandler
    {
        private ExternalEvent externalEvent;
        private Action<UIApplication> request;
        private readonly Stopwatch progressClock = new Stopwatch();
        private ((Guid Id, string Path) Project, string View)? viewContext;
        private bool supported;

        public ToolPane()
        {
            InitializeComponent();
        }

        internal void EnsureExternalEvent() { if (externalEvent == null) externalEvent = ExternalEvent.Create(this); }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }

        internal static ImageSource MakeIcon()
        {
            var drawing = new DrawingGroup();
            using (var ctx = drawing.Open())
            {
                var green = new SolidColorBrush(Color.FromRgb(23, 96, 90));
                ctx.DrawRoundedRectangle(green, null, new Rect(0, 0, 32, 32), 6, 6);
                var pen = new Pen(Brushes.White, 1.65);
                ctx.DrawLine(pen, new Point(6, 15), new Point(26, 15));
                ctx.DrawLine(pen, new Point(9, 9), new Point(9, 24));
                ctx.DrawLine(pen, new Point(23, 9), new Point(23, 24));
                ctx.DrawLine(pen, new Point(6, 18), new Point(12, 12));
                ctx.DrawLine(pen, new Point(20, 18), new Point(26, 12));
            }
            var img = new DrawingImage(drawing);
            img.Freeze();
            return img;
        }

        private static ((Guid Id, string Path) Project, string View)? CaptureContext(View view)
        {
            if (view == null || !view.IsValidObject) return null;
            var project = view.Document;
            // Revit may wrap the same document in different .NET objects across API callbacks.
            return ((project.CreationGUID, project.PathName.ToUpperInvariant()), view.UniqueId);
        }

        internal void ShowContext(View view)
        {
            supported = view is ViewPlan && !view.IsTemplate &&
                        (view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.EngineeringPlan);
            var previous = viewContext;
            viewContext = supported ? CaptureContext(view) : null;
            CurrentViewName.Text = supported ? view.Name : "平面図または構造平面図を開いてください";
            if (previous != viewContext)
            {
                HideProgress();
                ReviewList.ItemsSource = null;
                ReviewCard.Visibility = System.Windows.Visibility.Collapsed;
                StatusText.Text = supported ? "実行できます" : "対象ビューを開いてください";
            }
            if (supported)
            {
                var document = view.Document;
                bool sameProject = previous?.Project == viewContext?.Project;
                var oldType = sameProject ? (Styles.SelectedItem as DimensionType)?.Id : null;
                var types = new FilteredElementCollector(document).OfClass(typeof(DimensionType))
                    .Cast<DimensionType>().Where(t => t.StyleType == DimensionStyleType.Linear)
                    .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                Styles.ItemsSource = types;
                var defaultId = document.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType);
                Styles.SelectedItem = types.FirstOrDefault(t => t.Id == oldType) ??
                    types.FirstOrDefault(t => t.Id == defaultId) ?? types.FirstOrDefault();
                var tagTypes = MemberTags.Types(document).ToList();
                SetTagChoices(ColumnTagTypes, tagTypes.Where(t => MemberTags.IsTagType(t, true)), sameProject);
                SetTagChoices(BeamTagTypes, tagTypes.Where(t => MemberTags.IsTagType(t, false)), sameProject);
            }
            else
            {
                Styles.ItemsSource = null;
                ColumnTagTypes.ItemsSource = null;
                BeamTagTypes.ItemsSource = null;
            }
            UpdateRunButtons();
        }

        private static void SetTagChoices(ComboBox box, IEnumerable<FamilySymbol> types, bool preserve)
        {
            var previous = preserve ? SelectedTagId(box) : ElementId.InvalidElementId;
            var choices = types.Select(t => new { t.Id, Label = t.FamilyName + " / " + t.Name })
                .OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id.Value).ToList();
            choices.Insert(0, new { Id = ElementId.InvalidElementId,
                Label = choices.Count == 0 ? "タグタイプ未ロード" : "記入しない" });
            box.ItemsSource = choices;
            box.SelectedValue = choices.Any(t => t.Id == previous) ? previous : ElementId.InvalidElementId;
        }

        private static ElementId SelectedTagId(ComboBox box) => box?.SelectedValue as ElementId ?? ElementId.InvalidElementId;

        private void TagSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRunButtons();

        private void UpdateRunButtons()
        {
            bool ready = supported && request == null;
            if (RunButton != null) RunButton.IsEnabled = ready && Styles.SelectedItem != null;
            if (TagButton != null) TagButton.IsEnabled = ready &&
                (SelectedTagId(ColumnTagTypes) != ElementId.InvalidElementId || SelectedTagId(BeamTagTypes) != ElementId.InvalidElementId);
        }

        internal void Close() { externalEvent?.Dispose(); externalEvent = null; }
        public string GetName() => "床伏図の自動寸法";

        public void Execute(UIApplication app)
        {
            var action = request;
            try { action?.Invoke(app); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                HideProgress();
                StatusText.Text = "範囲の指定をキャンセルしました。";
            }
            catch (Exception ex)
            {
                HideProgress();
                StatusText.Text = "処理を完了できませんでした：" + ex.Message;
            }
            finally
            {
                request = null;
                UpdateRunButtons();
                UpdateReviewActions();
            }
        }

        private void Enqueue(Action<UIApplication> action, string status)
        {
            if (request != null) return;
            if (externalEvent == null)
            {
                StatusText.Text = "初期化できませんでした。「床伏図ツール」の「自動寸法」から開き直してください。";
                return;
            }
            request = action;
            var state = externalEvent.Raise();
            if (state != ExternalEventRequest.Accepted)
            {
                request = null;
                StatusText.Text = "Revit が処理中です。しばらくしてから再実行してください。";
                return;
            }
            UpdateRunButtons();
            UpdateReviewActions();
            StatusText.Text = status;
        }

        private void RunClick(object sender, RoutedEventArgs e) => StartRun(null);

        private void TagClick(object sender, RoutedEventArgs e)
        {
            var columnId = SelectedTagId(ColumnTagTypes);
            var beamId = SelectedTagId(BeamTagTypes);
            if (columnId == ElementId.InvalidElementId && beamId == ElementId.InvalidElementId) return;
            HideProgress();
            bool full = WholeView.IsChecked == true;
            var expected = viewContext;
            Enqueue(app =>
            {
                var ui = app.ActiveUIDocument;
                if (!expected.HasValue || CaptureContext(ui?.ActiveView) != expected)
                { StatusText.Text = "ビューが切り替わりました。現在のビューで再実行してください。"; return; }
                var column = columnId == ElementId.InvalidElementId ? null : ui.Document.GetElement(columnId) as FamilySymbol;
                var beam = beamId == ElementId.InvalidElementId ? null : ui.Document.GetElement(beamId) as FamilySymbol;
                if ((columnId != ElementId.InvalidElementId && !MemberTags.IsTagType(column, true)) ||
                    (beamId != ElementId.InvalidElementId && !MemberTags.IsTagType(beam, false)))
                {
                    ShowContext(ui.ActiveView);
                    StatusText.Text = "選択したタグタイプを使用できません。再選択してください。";
                    return;
                }
                var run = MemberTags.Run(ui, full, column, beam, ReportProgress);
                var status = $"対象部材 {run.Targets} 件 / 作成したタグ {run.Created} 件\n既存タグ {run.Existing} 件 / 要確認 {run.Notes.Count} 件";
                ShowResult(run.Notes, status);
            }, full ? "実行を待っています…" : "作図領域で範囲を指定してください。Esc キーでキャンセルできます。");
        }

        private void ApplyPairClick(object sender, RoutedEventArgs e)
        {
            if (ReviewList.SelectedItem is ReviewItem item && item.Pair != null) StartRun(item.Pair);
        }

        private void StartRun(ColumnPair reviewedPair)
        {
            HideProgress();
            if (!(Styles.SelectedItem is DimensionType style)) return;
            var enabled = reviewedPair != null ? new HashSet<string> { "Columns" } :
                new HashSet<string>(new[] { Axes, Columns, Beams, Walls, Floors, Openings }
                    .Where(c => c.IsEnabled && c.IsChecked == true).Select(c => c.Name));
            if (enabled.Count == 0) { StatusText.Text = "対象項目を 1 つ以上選択してください。"; return; }
            double offset = 0, columnOffset = 0, columnRowGap = 0;
            if (enabled.Any(role => role != "Columns") && !ReadDistance(OffsetBox, "1段目の離れ・柱以外", out offset)) return;
            if (enabled.Contains("Columns") &&
                (!ReadDistance(ColumnOffsetBox, "柱の1段目の離れ", out columnOffset) ||
                 !ReadDistance(ColumnRowGapBox, "柱の寸法線間隔", out columnRowGap))) return;
            bool full = WholeView.IsChecked == true;
            bool move = MoveTags.IsChecked == true;
            var beamWidth = Enum.TryParse<BeamWidthMode>((BeamWidths.SelectedItem as ComboBoxItem)?.Tag as string, out var width)
                ? width : BeamWidthMode.Overall;
            var expected = viewContext;
            ElementId selectedType = style.Id;

            Enqueue(app =>
            {
                var ui = app.ActiveUIDocument;
                if (!expected.HasValue || CaptureContext(ui?.ActiveView) != expected)
                { StatusText.Text = "ビューが切り替わりました。現在のビューで再実行してください。"; return; }
                var currentType = ui.Document.GetElement(selectedType) as DimensionType;
                if (currentType == null) { StatusText.Text = "選択した寸法タイプを使用できません。再選択してください。"; return; }
                var run = AnnotationEngine.Run(ui, currentType, full, enabled, offset, (columnOffset, columnRowGap),
                    move, beamWidth, ReportProgress, reviewedPair);
                var review = run.Notes;
                if (reviewedPair != null && ReviewList.ItemsSource is IEnumerable<ReviewItem> previous)
                {
                    bool handled = run.Created > 0 || run.Existing > 0;
                    review = previous.Where(item => !handled || item.PairingConcrete != reviewedPair.Concrete)
                        .Concat(review).ToList();
                }
                var status = $"対象部材 {run.Targets} 件 / 作成した寸法 {run.Created} 件\n既存・共用 {run.Existing} 件 / 移動した梁タグ {run.MovedLabels} 件\n要確認 {review.Count} 件";
                if (run.UnderSlabBeams > 0) status += $"\n追加検出した床下の梁 {run.UnderSlabBeams} 件";
                if (run.BeamChains > 0) status += $"\n作成した梁の連続寸法 {run.BeamChains} 件";
                ShowResult(review, status);
            }, full || reviewedPair != null ? "実行を待っています…" : "作図領域で範囲を指定してください。Esc キーでキャンセルできます。");
        }

        private void ShowResult(List<ReviewItem> review, string status)
        {
            ProgressText.Text = "処理完了";
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = RunProgress.Maximum;
            ReviewList.ItemsSource = review;
            ReviewCard.Visibility = review.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            StatusText.Text = status;
        }

        private bool ReadDistance(TextBox field, string label, out double distance)
        {
            if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out distance) &&
                distance > 0 && !double.IsInfinity(distance)) return true;
            StatusText.Text = $"{label}には、0 より大きい数値（mm）を入力してください。";
            field.Focus();
            field.SelectAll();
            return false;
        }

        private void ReportProgress(string phase, int completed, int total)
        {
            if (completed > 0 && progressClock.ElapsedMilliseconds < 120) return;
            progressClock.Restart();
            if (ProgressCard.Visibility == System.Windows.Visibility.Collapsed) StatusText.Text = "";
            ProgressCard.Visibility = System.Windows.Visibility.Visible;
            ProgressText.Text = total > 0 ? $"{phase}　{completed:N0} / {total:N0}" : phase;
            RunProgress.IsIndeterminate = total == 0;
            RunProgress.Maximum = Math.Max(total, 1);
            RunProgress.Value = completed;
            // Repaint at a checkpoint on Revit's UI thread. Some modal states defer dispatcher processing.
            try { Dispatcher.Invoke(() => { }, DispatcherPriority.Render); }
            catch (InvalidOperationException) { }
        }

        private void HideProgress()
        {
            ProgressCard.Visibility = System.Windows.Visibility.Collapsed;
            RunProgress.IsIndeterminate = false;
        }

        private void ReviewSelection(object sender, SelectionChangedEventArgs e) => UpdateReviewActions();

        private void UpdateReviewActions()
        {
            var item = ReviewList.SelectedItem as ReviewItem;
            if (LocateButton != null) LocateButton.IsEnabled = request == null && item?.Target != null;
            if (PairButton == null) return;
            PairButton.Visibility = item?.Pair != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            PairButton.IsEnabled = request == null && item?.Pair != null;
        }

        private void LocateClick(object sender, RoutedEventArgs e)
        {
            if (!(ReviewList.SelectedItem is ReviewItem item) || item.Target == null) return;
            var target = item.Target;
            var expected = viewContext;
            Enqueue(app =>
            {
                var ui = app.ActiveUIDocument;
                if (!expected.HasValue || CaptureContext(ui?.ActiveView) != expected)
                { StatusText.Text = "ビューが切り替わりました。現在のビューで再実行してください。"; return; }
                var windows = ui.GetOpenUIViews();
                var window = windows.FirstOrDefault(v => v.ViewId == ui.ActiveView.Id);
                var points = ModelGeometry.BoxPoints(target.Element.get_BoundingBox(null)).Select(target.Transform.OfPoint).ToList();
                if (window != null && points.Count > 0)
                {
                    double pad = ModelGeometry.Mm(900);
                    window.ZoomAndCenterRectangle(
                        new XYZ(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad, points[0].Z),
                        new XYZ(points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad, points[0].Z));
                }
                ui.Selection.SetReferences(new[] { target.SelectionReference });
                StatusText.Text = "表示中：" + target.Name;
            }, "選択した部材を表示しています…");
        }
    }
}
