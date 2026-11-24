using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using static RevitAutoAnnotate.ModelGeometry;

namespace RevitAutoAnnotate
{
    internal static class MemberTags
    {
        private static readonly BuiltInCategory[] memberCategories =
        {
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralFraming
        };

        public static IEnumerable<FamilySymbol> Types(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .WherePasses(new ElementMulticategoryFilter(new[]
                {
                    BuiltInCategory.OST_StructuralColumnTags, BuiltInCategory.OST_ColumnTags,
                    BuiltInCategory.OST_StructuralFramingTags, BuiltInCategory.OST_MultiCategoryTags
                })).Cast<FamilySymbol>();

        public static bool IsTagType(FamilySymbol symbol, bool column) =>
            symbol != null && IsTagCategory(DimensionPlanner.Category(symbol), column);

        private static bool IsTagCategory(BuiltInCategory category, bool column) =>
            category == BuiltInCategory.OST_MultiCategoryTags ||
            (column
                ? category == BuiltInCategory.OST_StructuralColumnTags || category == BuiltInCategory.OST_ColumnTags
                : category == BuiltInCategory.OST_StructuralFramingTags);

        private static bool CanTag(BuiltInCategory tag, BuiltInCategory member) =>
            tag == BuiltInCategory.OST_MultiCategoryTags ||
            (member == BuiltInCategory.OST_StructuralColumns && tag == BuiltInCategory.OST_StructuralColumnTags) ||
            (member == BuiltInCategory.OST_Columns && tag == BuiltInCategory.OST_ColumnTags) ||
            (member == BuiltInCategory.OST_StructuralFraming && tag == BuiltInCategory.OST_StructuralFramingTags);

        private static (long Link, long Member) MemberKey(LinkElementId id) =>
            (id.LinkInstanceId.Value, (id.LinkInstanceId == ElementId.InvalidElementId ? id.HostElementId : id.LinkedElementId).Value);

        private static (long Link, long Member) TargetKey(Target target) => (target.LinkId, target.Element.Id.Value);

        public static AnnotationRun Run(UIDocument ui, bool entireView, FamilySymbol column, FamilySymbol beam,
            Action<string, int, int> progress)
        {
            var doc = ui.Document;
            var view = ui.ActiveView;
            var frame = new ViewFrame(view);
            var crop = view.CropBoxActive ? Rect2.From(BoxPoints(view.CropBox), frame) : null;
            Rect2 region = null;
            if (!entireView)
            {
                var picked = ui.Selection.PickBox(PickBoxStyle.Crossing, "タグを作成する範囲を指定してください。Esc キーでキャンセル");
                region = Rect2.From(new[] { picked.Min, picked.Max }, frame);
            }

            progress("柱・梁と既存のタグを確認中…", 0, 0);
            var result = new AnnotationRun();
            var targets = new List<(Target Target, FamilySymbol Symbol, List<XYZ> Corners)>();
            var filter = new ElementMulticategoryFilter(memberCategories);
            void Collect(FilteredElementCollector collector, RevitLinkInstance link)
            {
                try
                {
                    foreach (var e in collector.WherePasses(filter).WhereElementIsNotElementType())
                    {
                        bool isColumn = DimensionPlanner.IsColumn(e);
                        var symbol = isColumn ? column : beam;
                        if (symbol == null || !CanTag(DimensionPlanner.Category(symbol), DimensionPlanner.Category(e))) continue;
                        if (!isColumn && e is FamilyInstance instance &&
                            (instance.StructuralType == StructuralType.Brace || IsColumnFukashi(instance))) continue;
                        var target = new Target(link, e);
                        var corners = BoxPoints(e.get_BoundingBox(link == null ? view : null)).Select(target.Transform.OfPoint).ToList();
                        var box = Rect2.From(corners, frame);
                        if (box == null || (region != null && !region.Intersects(box)) || (crop != null && !crop.Intersects(box)))
                            continue;
                        targets.Add((target, symbol, corners));
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    result.Notes.Add(new ReviewItem { Message = (link?.Name ?? doc.Title) + " · 表示部材を取得できませんでした：" + ex.Message });
                }
            }
            Collect(new FilteredElementCollector(doc, view.Id), null);
            var links = new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Where(l => !l.IsHidden(view) && l.GetLinkDocument() != null).ToList();
            foreach (var link in links)
                Collect(new FilteredElementCollector(doc, view.Id, link.Id), link);

            var owners = new Dictionary<Target, Target>();
            var columns = targets.Select(t => t.Target).Where(t => DimensionPlanner.IsColumn(t.Element)).ToList();
            if (columns.Count > 1)
            {
                var planner = new DimensionPlanner(doc, view, new FamilyMap(), 0, (0, 0), BeamWidthMode.Omit, result.Notes);
                owners = planner.ColumnTagOwners(columns);
            }
            var aliases = owners.ToDictionary(p => TargetKey(p.Key), p => TargetKey(p.Value));
            (long, long) Owner((long, long) key)
            {
                while (aliases.TryGetValue(key, out var parent)) key = parent;
                return key;
            }
            targets.RemoveAll(t => owners.ContainsKey(t.Target));
            result.Targets = targets.Count;
            if (targets.Count == 0)
            {
                result.Notes.Add(new ReviewItem { Message = "指定範囲に、選択したタグタイプでタグ付けできる部材がありません。" });
                return result;
            }
            var tags = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                .Where(t => !t.IsOrphaned && !t.IsHidden(view)).ToList();
            var tagged = new HashSet<(long, long)>(tags
                .Where(t => IsTagCategory(DimensionPlanner.Category(t), true) || IsTagCategory(DimensionPlanner.Category(t), false))
                .SelectMany(t => t.GetTaggedElementIds()).Select(id => Owner(MemberKey(id))));
            var occupied = tags.Cast<Element>().Concat(new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)))
                .Select(t => Rect2.From(BoxPoints(t.get_BoundingBox(view)), frame)).Where(b => b != null).ToList();
            occupied.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>()
                .Where(d => d.AreReferencesAvailable && !d.IsHidden(view))
                .SelectMany(d => AnnotationEngine.DimensionTexts(d, frame, view.Scale)).Select(t => t.Box));

            using (var group = new TransactionGroup(doc, "柱・梁タグを作成"))
            {
                if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("タグ作成を開始できませんでした。");
                int count = 0;
                foreach (var (target, symbol, corners) in targets.OrderBy(t => t.Target.Link?.Id.Value).ThenBy(t => t.Target.Element.Id.Value))
                {
                    progress("柱・梁タグを配置中…", count++, targets.Count);
                    var key = TargetKey(target);
                    if (tagged.Contains(key)) { result.Existing++; continue; }
                    string error;
                    var box = Place(doc, view, target, symbol, corners, frame, crop, occupied, out error);
                    if (box == null)
                        result.Notes.Add(new ReviewItem { Target = target, Message = target.Name + " · " + error });
                    else
                    {
                        tagged.Add(key);
                        occupied.Add(box);
                        result.Created++;
                    }
                }
                if (result.Created == 0) group.RollBack();
                else if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("タグ作成を確定できませんでした。");
            }
            return result;
        }

        private static Rect2 Place(Document doc, View view, Target target, FamilySymbol symbol, List<XYZ> corners,
            ViewFrame frame, Rect2 crop, List<Rect2> occupied, out string error)
        {
            error = "周辺にタグを置ける空きが見つかりませんでした。";
            if (view.GetCategoryHidden(symbol.Category.Id))
            {
                error = "選択したタグのカテゴリが、このビューでは非表示です。";
                return null;
            }
            var columnOutline = DimensionPlanner.IsColumn(target.Element) ? Rect2.From(corners, frame) : null;
            double cornerInset = Mm(0.5 * view.Scale);
            var (orientation, positions) = Positions(target, corners, frame, view.Scale, columnOutline != null);
            if (columnOutline != null) error = "柱の左下にタグを置ける空きが見つかりませんでした。";
            Rect2 TagBox(IndependentTag tag) => Rect2.From(BoxPoints(tag.get_BoundingBox(view)), frame);
            bool IsClear(Rect2 box) => box != null && (crop == null || crop.Contains(box)) &&
                !occupied.Any(box.Intersects);
            using (var tx = new Transaction(doc, "部材タグを配置"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("タグ配置を開始できませんでした。");
                var failures = new RollBackOnRevitFailures();
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                try
                {
                    IndependentTag tag = null;
                    foreach (var position in positions)
                    {
                        if (columnOutline == null && crop != null && !crop.Contains(Rect2.Around(frame.ToLocal(position), 0, 0)))
                            continue;
                        if (tag == null)
                            tag = IndependentTag.Create(doc, symbol.Id, view.Id, target.SelectionReference, false, orientation, position);
                        else tag.TagHeadPosition = position;
                        if (tag == null) break;
                        doc.Regenerate();
                        var box = TagBox(tag);
                        if (columnOutline != null && box != null)
                        {
                            var head = tag.TagHeadPosition;
                            var measured = box;
                            box = null;
                            foreach (var shift in ColumnCornerShifts(columnOutline, measured, cornerInset))
                            {
                                var atCorner = measured.Translate(shift);
                                if (!IsClear(atCorner)) continue;
                                var wanted = head + frame.Right * shift.X + frame.Up * shift.Y;
                                tag.TagHeadPosition = wanted;
                                doc.Regenerate();
                                if (tag.TagHeadPosition.DistanceTo(wanted) > Tolerance) continue;
                                // Revit can include the host attachment point in the box even without a visible leader.
                                box = atCorner;
                                break;
                            }
                        }
                        if (!IsClear(box)) continue;
                        if (tx.Commit() == TransactionStatus.Committed) return box;
                        error = failures.Message ?? "タグを確定できませんでした。";
                        break;
                    }
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { error = ex.Message; }
                finally { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); }
            }
            return null;
        }

        private static IEnumerable<Point2> ColumnCornerShifts(Rect2 column, Rect2 tag, double inset)
        {
            var corner = column.LowerLeft - tag.LowerLeft;
            foreach (var (x, y) in new[] { (1, 1), (2, 1), (1, 2), (3, 1), (1, 3), (2, 2) })
            {
                var shift = new Point2(corner.X + x * inset, corner.Y + y * inset);
                if (column.Contains(tag.Translate(shift))) yield return shift;
            }
        }

        private static (TagOrientation, IEnumerable<XYZ>) Positions(Target target, List<XYZ> corners, ViewFrame frame,
            int scale, bool column)
        {
            double gap = Mm(3 * scale);
            if (!column && target.Element.Location is LocationCurve location)
            {
                var curve = location.Curve;
                var axis = target.Transform.OfVector(curve.GetEndPoint(1) - curve.GetEndPoint(0));
                bool vertical = Math.Abs(axis.DotProduct(frame.Up)) > Math.Abs(axis.DotProduct(frame.Right));
                var side = vertical ? frame.Right : frame.Up;
                var points = new[] { 0.5, 0.4, 0.6, 0.3, 0.7, 0.2, 0.8, 0.1, 0.9 }
                    .SelectMany(fraction =>
                    {
                        var point = target.Transform.OfPoint(curve.Evaluate(fraction, true));
                        return new[] { point + side * gap, point - side * gap };
                    });
                return (vertical ? TagOrientation.Vertical : TagOrientation.Horizontal, points);
            }
            var center = corners.Aggregate(XYZ.Zero, (sum, p) => sum + p) / corners.Count;
            if (column) return (TagOrientation.Horizontal, new[] { center });
            var positions = new[] { frame.Right, -frame.Right, frame.Up, -frame.Up }.Select(side =>
                center + side * (corners.Max(p => (p - center).DotProduct(side)) + gap));
            return (TagOrientation.Horizontal, positions);
        }
    }
}
