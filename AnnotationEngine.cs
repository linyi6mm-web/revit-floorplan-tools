using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using static RevitAutoAnnotate.ModelGeometry;

namespace RevitAutoAnnotate
{
    internal sealed class AnnotationRun
    {
        public int Created, Existing, MovedLabels, Targets, UnderSlabBeams, BeamChains;
        public readonly List<ReviewItem> Notes = new List<ReviewItem>();
    }

    internal sealed class RollBackOnRevitFailures : IFailuresPreprocessor
    {
        public string Message { get; private set; }
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var messages = accessor.GetFailureMessages();
            if (messages.Count == 0) return FailureProcessingResult.Continue;
            Message = string.Join(" / ", messages.Select(m => m.GetDescriptionText()).Distinct());
            return FailureProcessingResult.ProceedWithRollBack;
        }
    }

    internal static class AnnotationEngine
    {
        private static readonly BuiltInCategory[] categories =
        {
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_ShaftOpening,
            BuiltInCategory.OST_FloorOpening, BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_Doors
        };
        private static readonly BuiltInCategory[] componentCategories =
        {
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Walls
        };

        public static AnnotationRun Run(UIDocument ui, DimensionType dimensionType, bool entireView,
            HashSet<string> enabled, double offset, (double FirstRow, double RowGap) columnSpacingMm, bool moveTags,
            BeamWidthMode beamWidth, Action<string, int, int> progress, ColumnPair reviewedPair = null)
        {
            Document doc = ui.Document;
            View view = ui.ActiveView;
            var result = new AnnotationRun();
            var map = new FamilyMap();
            Rect2 region = null;
            if (!entireView && reviewedPair == null)
            {
                var picked = ui.Selection.PickBox(PickBoxStyle.Crossing, "寸法を作成する範囲を指定してください。Esc キーでキャンセル");
                region = Rect2.From(new[] { picked.Min, picked.Max }, new ViewFrame(view));
            }

            progress("リンク部材を確認中…", 0, 0);
            var frame = new ViewFrame(view);
            var crop = view.CropBoxActive ? Rect2.From(BoxPoints(view.CropBox), frame) : null;
            var targets = new List<Target>();
            var components = new List<Rect2>();
            var contextBeams = new List<Target>();
            var contextFloors = new List<Target>();
            var contextColumns = new List<Target>();
            var elementFilter = new ElementMulticategoryFilter(categories);
            var links = new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Where(l => !l.IsHidden(view)).OrderBy(l => l.Id.Value).ToList();
            foreach (var link in links)
            {
                var linked = link.GetLinkDocument();
                if (linked == null) continue;
                try
                {
                    foreach (var e in new FilteredElementCollector(doc, view.Id, link.Id)
                                 .WherePasses(elementFilter).WhereElementIsNotElementType())
                    {
                        var target = new Target(link, e);
                        bool component = componentCategories.Contains(DimensionPlanner.Category(e));
                        string role = Role(e, map);
                        if (role == null && !component) continue;
                        var box = Rect2.From(BoxPoints(e.get_BoundingBox(null)).Select(target.Transform.OfPoint), frame);
                        if (box == null || (crop != null && !crop.Intersects(box))) continue;
                        if (component) components.Add(box);
                        if (role == "Beams") contextBeams.Add(target);
                        if (role == "Floors") contextFloors.Add(target);
                        if (role == "Columns" && enabled.Contains("Columns")) contextColumns.Add(target);
                        if ((region == null || region.Intersects(box)) && enabled.Contains(role)) targets.Add(target);
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    result.Notes.Add(new ReviewItem { Message = link.Name + " · 表示されている部材を取得できませんでした：" + ex.Message });
                }
            }
            if (reviewedPair != null)
            {
                Target Resolve((long Link, string Element) id) => contextColumns.FirstOrDefault(t =>
                    t.Link.Id.Value == id.Link && t.Element.UniqueId == id.Element);
                var concrete = Resolve(reviewedPair.ConcreteId);
                var steel = Resolve(reviewedPair.SteelId);
                if (concrete == null || steel == null)
                {
                    result.Notes.Add(new ReviewItem { Message = "選択した候補が現在のビューにありません。寸法作成を実行し直してください。" });
                    return result;
                }
                progress("鉄骨の寸法位置を計算中…", 0, 0);
                var reviewedPlanner = new DimensionPlanner(doc, view, map, offset, columnSpacingMm, beamWidth, result.Notes);
                var steelPlans = reviewedPlanner.BuildReviewedSteel(concrete, steel);
                result.Targets = 1;
                if (steelPlans.Count > 0)
                    Write(doc, view, frame, dimensionType, steelPlans, crop, components, false, result, progress);
                return result;
            }
            bool needNearbyBeams = enabled.Contains("Floors") || enabled.Contains("Openings");
            if (enabled.Contains("Beams") || needNearbyBeams)
            {
                var known = new HashSet<(long instance, long element)>(contextBeams.Select(t =>
                    (t.Link.Id.Value, t.Element.Id.Value)));
                var extra = UnderSlabBeamFinder.Find(doc, view, links, contextFloors, known, result.Notes);
                foreach (var beam in extra)
                {
                    var box = Rect2.From(BoxPoints(beam.Element.get_BoundingBox(null)).Select(beam.Transform.OfPoint), frame);
                    if (box == null || (crop != null && !crop.Intersects(box))) continue;
                    contextBeams.Add(beam);
                    if (enabled.Contains("Beams") && (region == null || region.Intersects(box)))
                    {
                        targets.Add(beam);
                        result.UnderSlabBeams++;
                    }
                }
            }
            result.Targets = targets.Count;
            if (targets.Count == 0 && !(entireView && enabled.Contains("Axes")))
            {
                result.Notes.Add(new ReviewItem { Message = "指定範囲に対象となるリンク部材がありません。" });
                return result;
            }
            progress("寸法を配置する位置を計算中…", 0, 0);
            var planner = new DimensionPlanner(doc, view, map, offset, columnSpacingMm, beamWidth, result.Notes);
            var plans = planner.Build(targets, needNearbyBeams ? contextBeams :
                contextBeams.Where(t => targets.Contains(t)).ToList(), contextFloors, contextColumns,
                entireView && enabled.Contains("Axes"));
            if (plans.Count > 0)
                Write(doc, view, frame, dimensionType, plans, crop, components, moveTags, result, progress);
            return result;
        }

        private static string Role(Element e, FamilyMap map)
        {
            if (e is Floor) return "Floors";
            if (e is Wall) return "Walls";
            var category = DimensionPlanner.Category(e);
            if (DimensionPlanner.IsColumn(e)) return "Columns";
            if (category == BuiltInCategory.OST_StructuralFraming)
                return e is FamilyInstance instance && IsColumnFukashi(instance) ? null : "Beams";
            if (e is Opening || map.Matches("Hatch", Target.TypeName(e)) || map.Matches("Elevator", Target.TypeName(e))) return "Openings";
            return null;
        }

        private static void Write(Document doc, View view, ViewFrame frame, DimensionType type, List<DimensionPlan> plans,
            Rect2 crop, List<Rect2> components, bool moveTags, AnnotationRun result, Action<string, int, int> progress)
        {
            const string phase = "寸法候補を処理中";
            progress(phase, 0, plans.Count);
            var existing = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension))
                .Cast<Dimension>().Where(d => d.AreReferencesAvailable).ToList();
            var keys = new HashSet<string>(existing.Select(d => Key(doc, d.References.Cast<Reference>())));
            var columnCoverage = plans.Any(p => p.IsColumn) ? new ColumnDimensionCoverage(view, existing) : null;
            var pending = plans.SelectMany(p => p.IsColumn
                ? columnCoverage.Partition(p) : new[] { (Plan: p, Exists: false) }).ToList();
            var axisSegments = new HashSet<(long, long)>();
            if (plans.Any(p => p.CheckExistingAxisSegment))
                foreach (var dim in existing.Where(d => d.Curve is Line))
                    AddAxisSegments(doc, dim.References.Cast<Reference>(), axisSegments);
            var occupied = existing.SelectMany(d => DimensionTexts(d, frame, view.Scale)).Select(t => t.Box).ToList();
            var labels = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                .Where(tag => !tag.IsOrphaned)
                .Select(tag => (tag, box: Rect2.From(BoxPoints(tag.get_BoundingBox(view)), frame))).Where(t => t.box != null).ToList();
            occupied.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
                .Select(e => Rect2.From(BoxPoints(e.get_BoundingBox(view)), frame)).Where(r => r != null));
            components.AddRange(new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(componentCategories)).WhereElementIsNotElementType()
                .Select(e => Rect2.From(BoxPoints(e.get_BoundingBox(view)), frame)).Where(r => r != null));
            var placed = new List<(Dimension Dimension, Line Line, DimensionPlan Plan, List<Rect2> Boxes)>();
            IEnumerable<Rect2> PlacedText(int except = -1) =>
                placed.Where((_, i) => i != except).SelectMany(item => item.Boxes);
            IEnumerable<Rect2> Annotations(int except = -1) =>
                occupied.Concat(labels.Select(t => t.box)).Concat(PlacedText(except));

            var accepted = new List<DimensionPlan>();
            double elevation = view.GenLevel?.ProjectElevation ?? frame.Origin.Z;
            var moved = new HashSet<ElementId>();
            using (var group = new TransactionGroup(doc, "床伏図の自動寸法"))
            {
                if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("寸法作成を開始できませんでした。");
                for (int index = 0; index < pending.Count; index++)
                {
                    progress(phase, index, pending.Count);
                    var (plan, exists) = pending[index];
                    if (plan.ReplacedBy != null && accepted.Contains(plan.ReplacedBy)) continue;
                    var refs = plan.Marks.Select(w => w.Reference).ToList();
                    string key = Key(doc, refs);
                    bool duplicate = plan.GridSide == 0 ? keys.Contains(key) :
                        existing.Any(d => d.Curve is Line ld &&
                            (ld.Origin.DotProduct(plan.Along) - plan.GridSpanCenter) * plan.GridSide > 0 &&
                            Key(doc, d.References.Cast<Reference>()) == key);
                    if (exists || duplicate || (plan.CheckExistingAxisSegment && axisSegments.Contains(AxisPair(refs[0], refs[1]))))
                    {
                        result.Existing++;
                        accepted.Add(plan);
                        continue;
                    }
                    if (plan.CheckSharedBoardEdge && Covered(plan, accepted))
                    {
                        result.Existing++;
                        continue;
                    }
                    Func<double, double, XYZ> point = (c, s) => plan.Direction * c + plan.Along * s + XYZ.BasisZ * elevation;
                    double start = plan.Marks.First().Coordinate, end = plan.Marks.Last().Coordinate;
                    if (end - start <= doc.Application.ShortCurveTolerance) continue;
                    int[] lanes = plan.BeamMiddle && !plan.BeamChainRange.HasValue
                        ? new[] { 0 } : new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5 };
                    double laneGap = Math.Max(Mm(200), Mm(view.Scale * 4));
                    var candidates = lanes.Select(i =>
                    {
                        double s = plan.Station + i * laneGap;
                        return (station: s, boxes: ProposedTextBounds(plan, point, s, frame, view.Scale));
                    }).ToList();
                    bool Visible(double s, List<Rect2> boxes) =>
                        (plan.GridSide == 0 || (s - plan.GridSpanCenter) * plan.GridSide > 0) &&
                        (!plan.BeamChainRange.HasValue ||
                            (s > plan.BeamChainRange.Value.Low && s < plan.BeamChainRange.Value.High)) &&
                        (crop == null || boxes.All(crop.Intersects));
                    var best = candidates.FirstOrDefault(c => Visible(c.station, c.boxes));
                    if (best.boxes == null)
                    {
                        AddNote(result, plan, "寸法値がトリミング範囲の外にあります。");
                        continue;
                    }
                    try
                    {
                        var line = Line.CreateBound(point(start, best.station), point(end, best.station));
                        var dim = Create(doc, view, type, line, refs, plan.Marks, out string message);
                        if (dim == null)
                        {
                            AddNote(result, plan, message);
                            continue;
                        }
                        keys.Add(key);
                        if (plan.Marks.Count(w => w.Grid) >= 2) AddAxisSegments(doc, refs, axisSegments);
                        accepted.Add(plan);
                        result.Created++;
                        if (plan.BeamChainRange.HasValue) result.BeamChains++;
                        var textBoxes = PlaceDimensionText(doc, dim, line, frame, view.Scale, Annotations(), components, crop,
                            plan.CenterWidthText);
                        if (textBoxes.Count == 0) textBoxes = best.boxes;
                        if (plan.BeamMiddle)
                        {
                            var lineBox = Rect2.From(new[] { line.GetEndPoint(0), line.GetEndPoint(1) }, frame);
                            var tagTargets = plan.ChainBeams ?? new List<Target> { plan.Target };
                            for (int i = 0; i < labels.Count; i++)
                            {
                                var entry = labels[i];
                                if (!lineBox.Intersects(entry.box)) continue;
                                var hostIds = entry.tag.GetTaggedElementIds();
                                var tagTarget = tagTargets.FirstOrDefault(t => hostIds.Any(id =>
                                    id.LinkInstanceId == t.Link.Id && id.LinkedElementId == t.Element.Id));
                                if (tagTarget == null || !moveTags || entry.tag.Pinned || moved.Contains(entry.tag.Id))
                                { AddNote(result, plan, "梁中央の寸法が既存のタグと重なっています。"); continue; }
                                var newBox = MoveTag(doc, view, frame, tagTarget, plan.Along, entry.tag, entry.box,
                                    occupied.Concat(PlacedText()).Concat(labels.Where(l => l.tag.Id != entry.tag.Id).Select(l => l.box))
                                        .Concat(textBoxes).Append(lineBox).ToList(), crop);
                                if (newBox == null) AddNote(result, plan, "梁タグを移動できる位置が見つかりません。");
                                else
                                {
                                    labels[i] = (entry.tag, newBox);
                                    moved.Add(entry.tag.Id);
                                    result.MovedLabels++;
                                }
                            }
                        }
                        placed.Add((dim, line, plan, textBoxes));
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex) { AddNote(result, plan, ex.Message); }
                    catch (InvalidOperationException ex) { AddNote(result, plan, ex.Message); }
                }
                // A later dimension may have no space on its own axis. Revisit earlier new dimensions
                // now that they can also see the later text, while preserving existing annotations.
                for (int i = 0; i < placed.Count; i++)
                {
                    progress("寸法値の重なりを確認中…", i, placed.Count);
                    var item = placed[i];
                    var obstacles = Annotations(i);
                    bool centerWidth = item.Plan.CenterWidthText;
                    if (!centerWidth && !TextsOverlap(item.Boxes, obstacles)) continue;
                    var adjusted = PlaceDimensionText(doc, item.Dimension, item.Line, frame, view.Scale, obstacles, components, crop,
                        centerWidth);
                    if (adjusted.Count > 0) placed[i] = (item.Dimension, item.Line, item.Plan, adjusted);
                }
                for (int i = 0; i < placed.Count; i++)
                    if (TextsOverlap(placed[i].Boxes, Annotations(i)))
                        AddNote(result, placed[i].Plan, "寸法線に沿って移動できる空きがありません。寸法値の位置を確認してください。");

                if (result.Created == 0) group.RollBack();
                else if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("寸法の作成を確定できませんでした。");
            }
        }

        private static void AddNote(AnnotationRun run, DimensionPlan plan, string message) =>
            run.Notes.Add(new ReviewItem { Target = plan.Target,
                Message = (plan.Target == null ? "" : plan.Target.Name + " · ") + plan.Label + "：" + message });

        private static bool Covered(DimensionPlan floor, List<DimensionPlan> accepted)
        {
            var grid = floor.Marks.FirstOrDefault(w => w.Grid);
            return grid != null && accepted.Any(p => p.Target != null && p.Target != floor.Target &&
                DimensionPlanner.Category(p.Target.Element) != BuiltInCategory.OST_Floors &&
                p.Direction.DotProduct(floor.Direction) > Parallel &&
                p.Marks.Any(w => w.Grid && w.Reference.ElementId == grid.Reference.ElementId) &&
                floor.Marks.Where(w => !w.Grid).All(w =>
                    p.Marks.Any(m => !m.Grid && Math.Abs(m.Coordinate - w.Coordinate) <= Tolerance)));
        }

        private static string Key(Document doc, IEnumerable<Reference> references)
        {
            return string.Join("|", references.Select(r => DimensionReferenceString(r.ConvertToStableRepresentation(doc)))
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        private static (long, long) AxisPair(Reference first, Reference second)
        {
            long a = first.ElementId.Value, b = second.ElementId.Value;
            return (Math.Min(a, b), Math.Max(a, b));
        }

        private static void AddAxisSegments(Document doc, IEnumerable<Reference> references, HashSet<(long, long)> segments)
        {
            Reference previous = null;
            foreach (var reference in references)
            {
                bool isAxis = reference.LinkedElementId == ElementId.InvalidElementId &&
                    doc.GetElement(reference.ElementId) is Grid;
                if (isAxis && previous != null) segments.Add(AxisPair(previous, reference));
                previous = isAxis ? reference : null;
            }
        }

        private static string DimensionReferenceString(string original)
        {
            var tokens = original.Split(':');
            int n = Array.FindIndex(tokens, s => s.Equals("RVTLINK", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("RVTLINK/", StringComparison.OrdinalIgnoreCase));
            if (n != 1 && n != 2) return original;
            if (n == 2 && tokens[1] != "0") return original;
            return tokens[0] + ":0:RVTLINK:" + string.Join(":", tokens.Skip(n + 1));
        }

        private static Dimension Create(Document doc, View view, DimensionType type, Line line, List<Reference> references,
            List<Witness> expected, out string message)
        {
            message = "指定された参照では Revit で寸法を作成できません。";
            for (int retry = 0; retry < 2; retry++)
            {
                using (var tx = new Transaction(doc, "寸法を作成"))
                {
                    if (tx.Start() != TransactionStatus.Started) return null;
                    var failures = new RollBackOnRevitFailures();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    try
                    {
                        var array = new ReferenceArray();
                        foreach (var r in references)
                        {
                            Reference witness = r;
                            if (retry != 0 && r.LinkedElementId != ElementId.InvalidElementId)
                            {
                                // ponytail: use the alternate RVTLINK encoding only after native linked references fail;
                                // revisit if other 2024 patch versions emit a different stable reference form.
                                string stable = r.ConvertToStableRepresentation(doc);
                                string alternate = DimensionReferenceString(stable);
                                if (alternate != stable)
                                    witness = Reference.ParseFromStableRepresentation(doc, alternate);
                            }
                            array.Append(witness);
                        }
                        var d = doc.Create.NewDimension(view, line, array, type);
                        if (d == null) { tx.RollBack(); continue; }
                        doc.Regenerate();
                        if (!d.AreReferencesAvailable)
                        {
                            message = "Revit で寸法の参照を解決できず、作成を取り消しました。";
                            tx.RollBack();
                            continue;
                        }
                        var theory = expected.Zip(expected.Skip(1), (a, b) => b.Coordinate - a.Coordinate).ToArray();
                        var actual = (d.Segments?.Size ?? 0) == 0 ? new[] { d.Value } :
                            d.Segments.Cast<DimensionSegment>().Select(s => s.Value).ToArray();
                        if (actual.Length != theory.Length || !actual.Zip(theory, (a, b) =>
                            a.HasValue && Math.Abs(a.Value - b) <= Tolerance * 2).All(a => a))
                        {
                            message = "寸法値が参照点間の距離と一致しないため、作成を取り消しました。";
                            tx.RollBack();
                            continue;
                        }
                        if (tx.Commit() == TransactionStatus.Committed) return d;
                        message = failures.Message ?? message;
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        throw;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        message = ex.Message;
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    }
                }
            }
            return null;
        }

        private static Rect2 MoveTag(Document doc, View view, ViewFrame frame, Target target, XYZ along, IndependentTag tag,
            Rect2 original, List<Rect2> obstacles, Rect2 crop)
        {
            if (!(target.Element.Location is LocationCurve loc) || !(loc.Curve is Line beam)) return null;
            var old = tag.TagHeadPosition;
            double start = target.Transform.OfPoint(beam.GetEndPoint(0)).DotProduct(along);
            double end = target.Transform.OfPoint(beam.GetEndPoint(1)).DotProduct(along);
            for (int i = 1; i <= 10; i++)
            foreach (int side in new[] { 1, -1 })
            {
                var delta = along * (side * i * Math.Max(Mm(150), Mm(view.Scale * 3)));
                double at = (old + delta).DotProduct(along);
                if (at < Math.Min(start, end) || at > Math.Max(start, end)) continue;
                var preview = original.Translate(frame.ToLocal(old + delta) - frame.ToLocal(old));
                if ((crop != null && !crop.Contains(preview)) || obstacles.Any(r => r.Intersects(preview))) continue;
                using (var tx = new Transaction(doc, "既存の梁タグを移動"))
                {
                    tx.Start();
                    var failures = new RollBackOnRevitFailures();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    try
                    {
                        tag.TagHeadPosition = old + delta;
                        doc.Regenerate();
                        var after = Rect2.From(BoxPoints(tag.get_BoundingBox(view)), frame) ?? preview;
                        if ((crop != null && !crop.Contains(after)) || obstacles.Any(r => r.Intersects(after)))
                        { tx.RollBack(); continue; }
                        if (tx.Commit() == TransactionStatus.Committed) return after;
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        throw;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        return null;
                    }
                }
            }
            return null;
        }

        private static List<Rect2> ProposedTextBounds(DimensionPlan p, Func<double, double, XYZ> point,
            double station, ViewFrame frame, int scale)
        {
            double halfHeight = Mm(1.8 * scale), halfWidth = Mm(5 * scale);
            return p.Marks.Zip(p.Marks.Skip(1), (a, b) =>
            {
                var c = frame.ToLocal(point((a.Coordinate + b.Coordinate) / 2, station));
                return Rect2.Around(c, halfWidth, halfHeight);
            }).ToList();
        }

        internal static List<(XYZ Position, Rect2 Box, Action<XYZ> Move)> DimensionTexts(Dimension d, ViewFrame frame, int scale)
        {
            var texts = new List<(XYZ Position, Rect2 Box, Action<XYZ> Move)>();
            if (!(d.Curve is Line line)) return texts;
            double x = Math.Abs(line.Direction.DotProduct(frame.Right));
            double y = Math.Abs(line.Direction.DotProduct(frame.Up));
            var type = d.DimensionType;
            double size = (type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? Mm(2.5)) * scale;
            double widthFactor = type?.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1;
            if (widthFactor <= 0) widthFactor = 1;
            void Add(XYZ position, string value, Action<XYZ> move)
            {
                if (position == null) return;
                // ponytail: estimate a text rectangle from the type size and value; use measured glyph extents if a font needs it.
                double width = Math.Max(2, value?.Length ?? 0) * size * 0.34 * widthFactor + Mm(0.5 * scale);
                double height = TextHalfHeight(size, scale);
                texts.Add((position, Rect2.Around(frame.ToLocal(position), x * width + y * height, y * width + x * height), move));
            }
            try
            {
                var segments = d.Segments;
                if ((segments?.Size ?? 0) == 0 && d.IsTextPositionAdjustable())
                    Add(d.TextPosition, d.ValueString, p => d.TextPosition = p);
                else if (segments != null)
                    foreach (DimensionSegment segment in segments)
                        if (segment.IsTextPositionAdjustable())
                            Add(segment.TextPosition, segment.ValueString, p => segment.TextPosition = p);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return texts;
        }

        private static List<Rect2> PlaceDimensionText(Document doc, Dimension dim, Line line, ViewFrame frame, int scale,
            IEnumerable<Rect2> annotations, List<Rect2> components, Rect2 crop, bool centerWidth)
        {
            var direction = line.Direction;
            var axis = new Point2(direction.DotProduct(frame.Right), direction.DotProduct(frame.Up));
            var texts = DimensionTexts(dim, frame, scale).OrderBy(t => t.Position.DotProduct(direction)).ToList();
            var original = texts.Select(t => t.Box).ToList();
            var boxes = original.ToArray();
            var positions = texts.Select(t => t.Position).ToArray();
            var changed = new List<int>();
            double center = line.Evaluate(0.5, true).DotProduct(direction);
            double step = Mm(0.5 * scale);
            const int steps = 40;
            bool OutsideCrop(Rect2 box) => crop != null && !crop.Contains(box);
            for (int i = 0; i < texts.Count; i++)
            {
                var box = boxes[i];
                double coordinate = positions[i].DotProduct(direction);
                double shift = centerWidth && texts.Count == 1
                    ? CenteredTextOffset(box, axis, line.Length, center - coordinate,
                        candidate => !OutsideCrop(candidate) && !annotations.Any(candidate.Intersects))
                    : 0;
                if (shift == 0)
                {
                    var siblings = boxes.Where((b, j) => j != i).ToList();
                    if (!NeedsTextShift(box, siblings, annotations)) continue;
                    var search = box.Swept(axis, step * steps);
                    var nearby = annotations.Concat(components).Where(search.Intersects).Concat(siblings).ToList();
                    double low = i == 0 ? double.NegativeInfinity : positions[i - 1].DotProduct(direction) - coordinate;
                    double high = i == texts.Count - 1 ? double.PositiveInfinity : positions[i + 1].DotProduct(direction) - coordinate;
                    // Search on the original text axis and keep the order of the segments.
                    shift = ClearTextOffset(box, axis, step, steps, low, high, coordinate < center ? -1 : 1,
                        candidate => !OutsideCrop(candidate) && !nearby.Any(candidate.Intersects));
                }
                if (shift == 0) continue;
                positions[i] += direction * shift;
                boxes[i] = box.Translate(axis * shift);
                changed.Add(i);
            }
            if (changed.Count == 0) return original;

            using (var tx = new Transaction(doc, "寸法値の位置を調整"))
            {
                if (tx.Start() != TransactionStatus.Started) return original;
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(new RollBackOnRevitFailures()).SetClearAfterRollback(true));
                try
                {
                    if (dim.HasLeader) dim.HasLeader = false;
                    foreach (int i in changed) texts[i].Move(positions[i]);
                    doc.Regenerate();
                    var actual = DimensionTexts(dim, frame, scale).OrderBy(t => t.Position.DotProduct(direction)).ToList();
                    if (actual.Count == positions.Length && actual.Select((t, i) => t.Position.DistanceTo(positions[i]) <= Tolerance).All(ok => ok) &&
                        tx.Commit() == TransactionStatus.Committed)
                        return actual.Select(t => t.Box).ToList();
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                catch (InvalidOperationException) { }
                finally { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); }
            }
            return original;
        }

        private static bool NeedsTextShift(Rect2 box, IEnumerable<Rect2> siblings, IEnumerable<Rect2> annotations) =>
            siblings.Any(box.Intersects) || annotations.Any(box.Intersects);

        private static bool TextsOverlap(List<Rect2> boxes, IEnumerable<Rect2> annotations) =>
            boxes.Where((box, i) => NeedsTextShift(box, boxes.Skip(i + 1), annotations)).Any();

        // Revit's text size is a full cap height. Keep a small clearance on both sides.
        private static double TextHalfHeight(double size, int scale) => size / 2 + Mm(0.25 * scale);

        private static double CenteredTextOffset(Rect2 box, Point2 axis, double span, double delta, Func<Rect2, bool> clear)
        {
            if (Math.Abs(delta) <= Tolerance || box.LengthAlong(axis) > span) return 0;
            return clear(box.Translate(axis * delta)) ? delta : 0;
        }

        private static double ClearTextOffset(Rect2 box, Point2 axis, double step, int steps, double low, double high,
            int preferred, Func<Rect2, bool> clear)
        {
            for (int i = 1; i <= steps; i++)
                foreach (int side in new[] { preferred, -preferred })
                {
                    double delta = side * i * step;
                    if (delta > low && delta < high && clear(box.Translate(axis * delta))) return delta;
                }
            return 0;
        }
    }

    internal sealed class Rect2
    {
        private readonly double left, bottom, right, top;
        public Rect2(double left, double bottom, double right, double top)
        { this.left = left; this.bottom = bottom; this.right = right; this.top = top; }
        public Point2 LowerLeft => new Point2(left, bottom);
        public static Rect2 From(IEnumerable<XYZ> points, ViewFrame frame)
        {
            var p = points.Select(frame.ToLocal).ToList();
            return p.Count == 0 ? null : new Rect2(p.Min(a => a.X), p.Min(a => a.Y), p.Max(a => a.X), p.Max(a => a.Y));
        }
        public static Rect2 Around(Point2 c, double x, double y) => new Rect2(c.X - x, c.Y - y, c.X + x, c.Y + y);
        public Rect2 Translate(Point2 d) => new Rect2(left + d.X, bottom + d.Y, right + d.X, top + d.Y);
        public double LengthAlong(Point2 axis) => (right - left) * Math.Abs(axis.X) + (top - bottom) * Math.Abs(axis.Y);
        public Rect2 Swept(Point2 axis, double distance)
        {
            double x = Math.Abs(axis.X * distance), y = Math.Abs(axis.Y * distance);
            return new Rect2(left - x, bottom - y, right + x, top + y);
        }
        public bool Intersects(Rect2 other) =>
            Math.Min(right, other.right) >= Math.Max(left, other.left) && Math.Min(top, other.top) >= Math.Max(bottom, other.bottom);
        public bool Contains(Rect2 other) => other.left >= left && other.right <= right && other.bottom >= bottom && other.top <= top;
    }
}
