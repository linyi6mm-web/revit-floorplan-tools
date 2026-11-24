using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using static RevitAutoAnnotate.ModelGeometry;
using Geometry = System.Windows.Media.Geometry;

namespace RevitAutoAnnotate
{
    [Transaction(TransactionMode.Manual)]
    public sealed class LinkedSelectionBox : IExternalCommand
    {
        internal const string CommandName = "リンク選択ボックス";
        private const string PreviewName = "無題";
        private const string LegacyPreviewName = "床伏図ツール - リンク選択ボックス";
        private static readonly Guid PreviewSchemaId = new Guid("B75CBCCF-453B-4D1A-8B07-6B309BB4FF92");

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var ui = data.Application.ActiveUIDocument;
            var doc = ui?.Document;
            if (doc == null || doc.IsFamilyDocument || doc.IsReadOnly)
            {
                TaskDialog.Show(CommandName, "編集可能な Revit プロジェクトを開いてください。");
                return Result.Cancelled;
            }

            try
            {
                var sourceView = ui.ActiveView;
                IEnumerable<Reference> selection = ui.Selection.GetReferences();
                if (!selection.Any(r => r.LinkedElementId != ElementId.InvalidElementId))
                    selection = ui.Selection.PickObjects(ObjectType.LinkedElement,
                        "リンク内の部材を選択し、［完了］をクリックしてください。Esc キーでキャンセル");

                var corners = selection.SelectMany(r => HostCorners(doc, r)).ToList();
                if (corners.Count == 0)
                {
                    TaskDialog.Show(CommandName, "切断範囲を取得できませんでした。リンク内の柱・梁などのモデル部材を選択してください。");
                    return Result.Cancelled;
                }

                using (var bounds = Enclose(corners, Mm(300)))
                {
                    var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().ToList();
                    var preview = FindPreview(views);
                    using (var tx = new Transaction(doc, CommandName))
                    {
                        if (tx.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("切断ボックスの設定を開始できませんでした。");
                        if (preview == null)
                        {
                            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                            if (type == null) throw new InvalidOperationException("3D ビューのタイプが見つかりません。");
                            preview = View3D.CreateIsometric(doc, type.Id);
                            preview.ViewTemplateId = ElementId.InvalidElementId;
                        }
                        if (preview.Name != PreviewName) preview.Name = PreviewName;
                        if (!IsOwnedPreview(preview)) MarkPreview(preview);
                        foreach (var key in new[] { BuiltInParameter.VIEW_PHASE, BuiltInParameter.VIEW_PHASE_FILTER })
                        {
                            var from = sourceView.get_Parameter(key);
                            var to = preview.get_Parameter(key);
                            if (from != null && to != null && !to.IsReadOnly && from.AsElementId() != to.AsElementId())
                                to.Set(from.AsElementId());
                        }
                        preview.SetSectionBox(bounds);
                        preview.IsSectionBoxActive = true;
                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("切断ボックスを設定できませんでした。");
                    }
                    if (ui.ActiveView.Id != preview.Id) ui.ActiveView = preview;
                    ui.GetOpenUIViews().FirstOrDefault(v => v.ViewId == preview.Id)?.ZoomToFit();
                }
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { message = ex.Message; return Result.Failed; }
            catch (InvalidOperationException ex) { message = ex.Message; return Result.Failed; }
        }

        private static View3D FindPreview(List<View3D> views)
        {
            var named = views.FirstOrDefault(v => v.Name == PreviewName);
            if (named != null)
            {
                if (named.IsTemplate || named.IsPerspective || !IsOwnedPreview(named))
                    throw new InvalidOperationException("「無題」は既存の 3D ビューで使われています。既存ビューの名前を変更してから再実行してください。");
                return named;
            }
            return views.FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective &&
                (IsOwnedPreview(v) || v.Name == LegacyPreviewName));
        }

        private static bool IsOwnedPreview(View3D view) => view.GetEntitySchemaGuids().Contains(PreviewSchemaId);

        private static void MarkPreview(View3D view)
        {
            var schema = Schema.Lookup(PreviewSchemaId);
            if (schema == null)
            {
                using (var builder = new SchemaBuilder(PreviewSchemaId))
                {
                    builder.SetSchemaName("FloorPlanToolsLinkedSelectionBox");
                    builder.SetReadAccessLevel(AccessLevel.Public);
                    builder.SetWriteAccessLevel(AccessLevel.Public);
                    builder.AddSimpleField("IsPreview", typeof(bool));
                    schema = builder.Finish();
                }
            }
            using (var owner = new Entity(schema))
            {
                owner.Set("IsPreview", true);
                view.SetEntity(owner);
            }
        }

        private static IEnumerable<XYZ> HostCorners(Document doc, Reference reference)
        {
            var element = doc.GetElement(reference.ElementId);
            if (element is RevitLinkInstance link)
            {
                if (reference.LinkedElementId == ElementId.InvalidElementId) return Array.Empty<XYZ>();
                var linked = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                if (linked == null || linked.ViewSpecific || linked is RevitLinkInstance) return Array.Empty<XYZ>();
                return BoxPoints(linked.get_BoundingBox(null)).Select(link.GetTotalTransform().OfPoint);
            }
            if (element?.Category?.CategoryType != CategoryType.Model || element.ViewSpecific) return Array.Empty<XYZ>();
            return BoxPoints(element.get_BoundingBox(null));
        }

        private static BoundingBoxXYZ Enclose(IList<XYZ> corners, double margin)
        {
            return new BoundingBoxXYZ
            {
                Min = new XYZ(corners.Min(p => p.X) - margin, corners.Min(p => p.Y) - margin, corners.Min(p => p.Z) - margin),
                Max = new XYZ(corners.Max(p => p.X) + margin, corners.Max(p => p.Y) + margin, corners.Max(p => p.Z) + margin)
            };
        }

        internal static ImageSource MakeIcon()
        {
            var bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 96, 90));
            var drawing = new DrawingGroup();
            using (var ctx = drawing.Open())
            {
                ctx.DrawRoundedRectangle(bg, null, new Rect(0, 0, 32, 32), 6, 6);
                ctx.DrawGeometry(null, new System.Windows.Media.Pen(Brushes.White, 1.65),
                    Geometry.Parse("M 16,5 L 26,10 L 26,22 L 16,27 L 6,22 L 6,10 Z M 6,10 L 16,15 L 26,10 M 16,15 L 16,27"));
            }
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }
}
