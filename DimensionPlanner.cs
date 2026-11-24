using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using static RevitAutoAnnotate.ModelGeometry;

namespace RevitAutoAnnotate
{
    internal enum BeamWidthMode { Overall, Split, Omit }

    internal sealed class DimensionPlan
    {
        public Target Target;
        public string Label;
        public XYZ Direction, Along;
        public double Station;
        public bool BeamMiddle;
        public (double Low, double High)? BeamChainRange;
        public List<Target> ChainBeams;
        public DimensionPlan ReplacedBy;
        public bool CheckSharedBoardEdge;
        public bool CheckExistingAxisSegment;
        public int GridSide;
        public double GridSpanCenter;
        public List<Witness> Marks;

        public bool IsColumn => Target != null && DimensionPlanner.IsColumn(Target.Element);
        public bool CenterWidthText => Marks.Count == 2 && !Marks.Any(w => w.Grid) && IsColumn;

        public DimensionPlan WithMarks(int start, int count)
        {
            if (count == Marks.Count) return this;
            var part = (DimensionPlan)MemberwiseClone();
            part.Marks = Marks.GetRange(start, count);
            return part;
        }
    }

    internal sealed class ReviewItem
    {
        public Target Target;
        public Target PairingConcrete;
        public ColumnPair Pair;
        public string Message { get; set; }
    }

    internal sealed class DimensionPlanner
    {
        private readonly List<(Line line, Grid grid)> grids;
        private readonly FamilyMap map;
        private readonly List<DimensionPlan> plans = new List<DimensionPlan>();
        private readonly List<(Target target, ModelGeometry geometry, XYZ tangent)> beams =
            new List<(Target, ModelGeometry, XYZ)>();
        private readonly List<(Target Target, XYZ Along, XYZ Direction, double Low, double High,
            Witness Center, DimensionPlan SingleLocation)> beamPositions =
            new List<(Target, XYZ, XYZ, double, double, Witness, DimensionPlan)>();
        private readonly Dictionary<Target, ModelGeometry> geometry = new Dictionary<Target, ModelGeometry>();
        private readonly Dictionary<Target, DimensionPlan> beamWidthPlans = new Dictionary<Target, DimensionPlan>();
        private readonly Dictionary<Target, (Target Steel, double Elevation)> columnSteel =
            new Dictionary<Target, (Target, double)>();
        private readonly HashSet<Target> coincidentColumnCopies = new HashSet<Target>();
        private readonly Dictionary<(Document, ElementId), StructuralMaterialType> columnTypeMaterials =
            new Dictionary<(Document, ElementId), StructuralMaterialType>();
        private readonly XYZ x, y;
        private readonly View hostView;
        private readonly Dictionary<ElementId, View> linkedColumnViews = new Dictionary<ElementId, View>();
        private readonly double? planCut;
        private readonly double offset;
        private readonly double columnOffset, columnRowGap;
        private readonly BeamWidthMode beamWidth;
        private readonly List<ReviewItem> notes;

        public DimensionPlanner(Document document, View view, FamilyMap familyMap, double offsetMm,
            (double FirstRow, double RowGap) columnSpacingMm, BeamWidthMode beamWidth, List<ReviewItem> notes)
        {
            this.notes = notes; map = familyMap; offset = Mm(offsetMm); this.beamWidth = beamWidth; hostView = view;
            columnOffset = Mm(columnSpacingMm.FirstRow);
            columnRowGap = Mm(columnSpacingMm.RowGap);
            if (view is ViewPlan floorPlan)
                using (var range = floorPlan.GetViewRange())
                    if (document.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) is Level level)
                        planCut = level.ProjectElevation + range.GetOffset(PlanViewPlane.CutPlane);
            grids = new FilteredElementCollector(document, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => !g.IsCurved && g.Curve is Line).OrderBy(g => g.Id.Value)
                .Select(g => ((Line)g.Curve, g)).ToList();
            var viewRight = Flat(view.RightDirection).Normalize();
            var primary = grids.Select(g => Flat(g.line.Direction).Normalize())
                .OrderByDescending(d => grids.Count(g => Math.Abs(d.DotProduct(Flat(g.line.Direction).Normalize())) > Parallel))
                .FirstOrDefault() ?? viewRight;
            var perpendicular = XYZ.BasisZ.CrossProduct(primary);
            x = Math.Abs(primary.DotProduct(viewRight)) >= Math.Abs(perpendicular.DotProduct(viewRight)) ? primary : perpendicular;
            if (x.DotProduct(viewRight) < 0) x = -x;
            y = XYZ.BasisZ.CrossProduct(x);
        }


        public List<DimensionPlan> Build(List<Target> targets, List<Target> contextBeams, List<Target> contextFloors,
            List<Target> contextColumns, bool axisSpans)
        {
            if (axisSpans) GridSpans(targets);
            int afterGridSpans = plans.Count;
            PairColumns(contextColumns, new HashSet<Target>(targets));
            if (targets.Any(t => !(t.Element is Floor) && map.Matches("Elevator", Target.TypeName(t.Element))))
            {
                foreach (var board in contextFloors)
                {
                    try { GetGeometry(board); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Note(board, "床の形状を取得できませんでした：" + ex.Message); }
                }
            }
            foreach (var beam in contextBeams)
            {
                if (!(beam.Element is FamilyInstance) || !(beam.Element.Location is LocationCurve loc) || !(loc.Curve is Line line)) continue;
                try
                {
                    var axis = beam.Transform.OfVector(line.Direction);
                    if (Math.Abs(axis.Z) > 0.00001) continue;
                    var tangent = Flat(axis).Normalize();
                    if (Math.Max(Math.Abs(tangent.DotProduct(x)), Math.Abs(tangent.DotProduct(y))) < Parallel) continue;
                    var g = GetGeometry(beam);
                    beams.Add((beam, g, tangent));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Note(beam, "梁の形状を取得できませんでした：" + ex.Message); }
            }
            foreach (var target in targets.OrderBy(t => t.Element is Floor ? 1 :
                         map.Matches("Elevator", Target.TypeName(t.Element)) ? 2 : 0)
                         .ThenBy(t => t.Link.Id.Value).ThenBy(t => t.Element.Id.Value))
            {
                try
                {
                    if (target.Element is Floor floor) { FloorEdges(target, floor); continue; }
                    string type = Target.TypeName(target.Element);
                    if (map.Matches("Hatch", type))
                    {
                        if (target.Element is FamilyInstance hatch) Hatch(target, hatch);
                        else Note(target, "床点検口に、平面上の 2 方向を寸法記入できるファミリ中心参照がありません。");
                        continue;
                    }
                    if (map.Matches("Elevator", type))
                    {
                        Elevator(target);
                        continue;
                    }
                    if (target.Element is Opening)
                    {
                        Note(target, "開口の用途を判定できません。ファミリ名またはタイプ名と、床点検口・昇降路の対応を確認してください。");
                        continue;
                    }
                    if (target.Element is Wall wall) { WallDimensions(target, wall); continue; }
                    if (!(target.Element is FamilyInstance instance)) continue;
                    if (IsColumn(instance))
                    {
                        if (!coincidentColumnCopies.Contains(target)) Column(target, instance);
                    }
                    else if (Category(instance) == BuiltInCategory.OST_StructuralFraming)
                        Beam(target, instance);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Note(target, "部材の参照を取得できませんでした：" + ex.Message); }
                catch (InvalidOperationException ex) { Note(target, ex.Message); }
            }
            BeamChains(afterGridSpans);
            foreach (var width in beamWidthPlans.Values.Where(p => p.ReplacedBy != null))
            {
                plans.Remove(width);
                plans.Add(width);
            }
            return plans;
        }

        public static BuiltInCategory Category(Element e) => (BuiltInCategory)(e.Category?.Id.Value ?? 0);
        public static bool IsColumn(Element e) => Category(e) == BuiltInCategory.OST_StructuralColumns ||
            Category(e) == BuiltInCategory.OST_Columns;
        private void Note(Target t, string message, Target pairingConcrete = null) => notes.Add(new ReviewItem
            { Target = t, PairingConcrete = pairingConcrete, Message = (t == null ? "" : t.Name + " · ") + message });
        private ModelGeometry GetGeometry(Target t)
        {
            if (!geometry.TryGetValue(t, out var g)) geometry.Add(t, g = new ModelGeometry(t));
            return g;
        }

        private IEnumerable<Witness> Grids(XYZ direction)
        {
            foreach (var (line, grid) in grids)
                if (Math.Abs(Flat(line.Direction).Normalize().DotProduct(direction)) < 0.000001)
                    yield return new Witness(new Reference(grid), line.GetEndPoint(0).DotProduct(direction), true);
        }
        private Witness NearestGrid(XYZ direction, double coordinate) =>
            Grids(direction).OrderBy(w => Math.Abs(w.Coordinate - coordinate)).FirstOrDefault();

        private void GridSpans(List<Target> targets)
        {
            var boundaries = targets.SelectMany(t => BoxPoints(t.Element.get_BoundingBox(null)).Select(t.Transform.OfPoint)).ToList();
            if (boundaries.Count == 0)
                boundaries = grids.SelectMany(g => new[] { g.line.GetEndPoint(0), g.line.GetEndPoint(1) }).ToList();
            if (boundaries.Count == 0) return;
            foreach (var (direction, along) in new[] { (x, y), (y, x) })
            {
                var witnesses = Grids(direction).ToList();
                if (witnesses.Count < 2) continue;
                double start = boundaries.Min(p => p.DotProduct(along));
                double stop = boundaries.Max(p => p.DotProduct(along));
                foreach (int side in new[] { -1, 1 })
                {
                    int count = plans.Count;
                    Add(null, "通り芯間寸法", direction, along, side < 0 ? start - offset : stop + offset, false, witnesses);
                    if (plans.Count == count) continue;
                    plans.Last().GridSide = side;
                    plans.Last().GridSpanCenter = (start + stop) / 2;
                }
            }
        }

        private DimensionPlan Add(Target t, string label, XYZ direction, XYZ along, double station, bool beamMiddle,
            IEnumerable<Witness> source)
        {
            var refs = source.ToList();
            if (refs.Any(w => w?.Reference == null)) { Note(t, label + "に必要な参照が見つかりません。"); return null; }
            var sorted = refs.OrderBy(w => w.Coordinate).ThenByDescending(w => w.Grid).ToList();
            var unique = new List<Witness>();
            foreach (var reference in sorted)
            {
                if (unique.Count == 0 || reference.Coordinate - unique.Last().Coordinate > Tolerance)
                    unique.Add(reference);
            }
            if (unique.Count < 2) return null;
            var plan = new DimensionPlan { Target = t, Label = label, Direction = direction, Along = along,
                Station = station, BeamMiddle = beamMiddle, Marks = unique };
            plans.Add(plan);
            return plan;
        }

        private List<FaceTrace> SideFaces(ModelGeometry g, XYZ direction, XYZ along, double station, bool requireReference = true) =>
            g.Faces.Where(f => (!requireReference || f.Reference != null) && Math.Abs(f.Normal.DotProduct(direction)) > Parallel &&
                f.Points.Min(p => p.DotProduct(along)) - Tolerance <= station &&
                f.Points.Max(p => p.DotProduct(along)) + Tolerance >= station).ToList();

        private static List<Witness> Positions(IEnumerable<FaceTrace> faces, XYZ direction) =>
            faces.Select(f => FaceWitness(f, direction)).OrderBy(w => w.Coordinate)
                .Aggregate(new List<Witness>(), (list, w) =>
                {
                    if (list.Count == 0 || w.Coordinate - list.Last().Coordinate > Tolerance) list.Add(w);
                    return list;
                });

        private static double? Length(Element element, BuiltInParameter key)
        {
            foreach (Element e in new[] { element, element.Document.GetElement(element.GetTypeId()) })
            {
                var p = e?.get_Parameter(key);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > Tolerance)
                    return p.AsDouble();
            }
            return null;
        }

        private (Witness low, Witness high) Body(Target t, List<FaceTrace> faces, XYZ direction,
            Witness low, Witness high, double? width, bool depth, bool allowSideParameters,
            IEnumerable<Witness> alternateSides = null)
        {
            var element = t.Element;
            double? a = allowSideParameters ? map.Length(element, depth ? "DepthLow" : "SideLow") : null;
            double? b = allowSideParameters ? map.Length(element, depth ? "DepthHigh" : "SideHigh") : null;
            var positions = Positions(faces, direction);
            if (alternateSides != null)
                foreach (var side in alternateSides)
                    if (positions.All(p => Math.Abs(p.Coordinate - side.Coordinate) > Tolerance)) positions.Add(side);
            if ((a.HasValue || b.HasValue) && element is FamilyInstance family)
            {
                var transform = t.Transform.Multiply(family.GetTransform());
                var local = Flat(depth ? transform.BasisY : transform.BasisX).Normalize();
                // Cross section of a beam uses the family's lateral Y axis.
                if (Category(family) == BuiltInCategory.OST_StructuralFraming)
                    local = Flat(transform.BasisY).Normalize();
                if (Math.Abs(local.DotProduct(direction)) < Parallel) return (null, null);
                if (local.DotProduct(direction) < 0) (a, b) = (b, a);
            }
            if (a < 0 || b < 0) return (null, null);
            if (a.HasValue || b.HasValue)
            {
                if (a.HasValue && !b.HasValue && width.HasValue) b = high.Coordinate - low.Coordinate - a - width;
                if (b.HasValue && !a.HasValue && width.HasValue) a = high.Coordinate - low.Coordinate - b - width;
                a = a ?? 0; b = b ?? 0;
                if (a < -Tolerance || b < -Tolerance) return (null, null);
                var first = positions.FirstOrDefault(w => Math.Abs(w.Coordinate - low.Coordinate - a.Value) <= Tolerance);
                var last = positions.FirstOrDefault(w => Math.Abs(w.Coordinate - high.Coordinate + b.Value) <= Tolerance);
                if (element is FamilyInstance f)
                {
                    bool reverse = Flat(t.Transform.OfVector(depth || Category(f) == BuiltInCategory.OST_StructuralFraming
                        ? f.GetTransform().BasisY : f.GetTransform().BasisX)).DotProduct(direction) < 0;
                    string lowName = depth ? "Core Front" : "Core Left";
                    string highName = depth ? "Core Back" : "Core Right";
                    if (first == null) first = Named(t, f, reverse ? highName : lowName, low.Coordinate + a.Value);
                    if (last == null) last = Named(t, f, reverse ? lowName : highName, high.Coordinate - b.Value);
                }
                if (first == null || last == null || last.Coordinate - first.Coordinate <= Tolerance ||
                    (width.HasValue && Math.Abs(last.Coordinate - first.Coordinate - width.Value) > Tolerance))
                    return (null, null);
                return (first, last);
            }

            if (width.HasValue)
            {
                var pairs = (from p in positions from q in positions
                    where Math.Abs(q.Coordinate - p.Coordinate - width.Value) <= Tolerance
                    select (p, q)).ToList();
                return pairs.Count == 1 ? pairs[0] : (null, null);
            }
            var core = Positions(faces.Where(f => !f.Added), direction);
            if (core.Count == 2) return (core[0], core[1]);
            return (null, null);
        }

        private static Witness Named(Target t, FamilyInstance family, string name, double value)
        {
            var reference = family.GetReferenceByName(name);
            return reference == null ? null : new Witness(t.InHost(reference), value);
        }

        private Witness CenterPlane(Target t, FamilyInstance family, XYZ direction, double value)
        {
            var trans = t.Transform.Multiply(family.GetTransform());
            var fx = Flat(trans.BasisX).Normalize();
            var fy = Flat(trans.BasisY).Normalize();
            FamilyInstanceReferenceType role;
            if (Math.Abs(fx.DotProduct(direction)) > Parallel) role = FamilyInstanceReferenceType.CenterLeftRight;
            else if (Math.Abs(fy.DotProduct(direction)) > Parallel) role = FamilyInstanceReferenceType.CenterFrontBack;
            else return null;
            return FamilyPlane(t, family, role, value);
        }

        private static Witness FamilyPlane(Target t, FamilyInstance family, FamilyInstanceReferenceType role, double value)
        {
            var references = family.GetReferences(role);
            return references?.Count == 1 ? new Witness(t.InHost(references[0]), value) : null;
        }

        private static bool NamedSrc(FamilyInstance family) =>
            Contains(Target.TypeName(family), "SRC", "ＳＲＣ", "鉄骨鉄筋", "型钢混凝土");

        private StructuralMaterialType ColumnMaterial(FamilyInstance family)
        {
            var materialType = family.StructuralMaterialType;
            if (materialType != StructuralMaterialType.Undefined && materialType != StructuralMaterialType.Generic &&
                materialType != StructuralMaterialType.Other) return materialType;
            var key = (family.Document, family.GetTypeId());
            if (columnTypeMaterials.TryGetValue(key, out var cached)) return cached;
            var parameter = family.Symbol?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
            if (parameter?.StorageType == StorageType.ElementId && family.Document.GetElement(parameter.AsElementId()) is Material material)
            {
                string label = material.Name + " " + material.MaterialClass;
                if (ColumnSteelName(label)) materialType = StructuralMaterialType.Steel;
                else if (ConcreteName(label)) materialType = StructuralMaterialType.Concrete;
            }
            columnTypeMaterials.Add(key, materialType);
            return materialType;
        }

        private ColumnBounds? GetColumnBounds(Target target)
        {
            var points = BoxPoints(target.Element.get_BoundingBox(null)).Select(target.Transform.OfPoint).ToList();
            if (points.Count == 0) return null;
            var xs = points.Select(p => p.DotProduct(x)).ToList();
            var ys = points.Select(p => p.DotProduct(y)).ToList();
            return new ColumnBounds(xs.Min(), xs.Max(), ys.Min(), ys.Max(), points.Min(p => p.Z), points.Max(p => p.Z));
        }

        public List<DimensionPlan> BuildReviewedSteel(Target concrete, Target steel)
        {
            var outer = GetColumnBounds(concrete);
            var inner = GetColumnBounds(steel);
            var section = outer.HasValue && inner.HasValue ? outer.Value.CommonSection(inner.Value, planCut, Tolerance) : null;
            if (section.HasValue && EnclosedSteelCenterOffset(concrete, steel, section.Value.Elevation).HasValue)
                LinkedSteelColumn(steel, GetGeometry(concrete), section.Value.Elevation, ColumnFukashi(concrete));
            else Note(steel, "現在のモデルでは柱による包囲を確認できません。寸法作成を実行し直してください。");
            return plans;
        }

        private void NoteSteelCandidate(ColumnPair pair)
        {
            notes.Add(new ReviewItem
            {
                Target = pair.Steel,
                PairingConcrete = pair.Concrete,
                Pair = pair,
                Message = $"{pair.Steel.Name} · 柱 #{pair.Concrete.Element.Id.Value} の鉄骨候補 · 芯ずれ {pair.CenterOffset / Mm(1):0.#} mm"
            });
        }

        private void PairColumns(List<Target> context, HashSet<Target> selected)
        {
            var copies = new Dictionary<Target, Target>();
            var unverified = new HashSet<Target>();
            var verified = FindColumnPairs(context, selected, copies, unverified);
            coincidentColumnCopies.UnionWith(copies.Keys);
            var matches = verified.ToLookup(pair => pair.Concrete);
            var uses = verified.ToLookup(pair => pair.Steel);
            foreach (var group in matches.Where(g => selected.Contains(g.Key)))
            {
                var candidates = ColumnPair.Preferred(group, Tolerance);
                var best = candidates[0];
                var owners = ColumnPair.Preferred(uses[best.Steel], Tolerance);
                if (candidates.Count == 1 && owners.Count == 1 && owners[0] == best)
                {
                    columnSteel.Add(group.Key, (best.Steel, best.Elevation));
                    continue;
                }
                if (candidates.Count > 1)
                {
                    Note(group.Key, $"内蔵鉄骨の候補が {candidates.Count} 件あります。下の候補から部材を表示できます。", group.Key);
                    foreach (var candidate in candidates) NoteSteelCandidate(candidate);
                }
                else
                {
                    Note(group.Key, $"鉄骨候補 {best.Steel.Name} を共有する柱：" +
                        string.Join("、", owners.Select(owner => owner.Concrete.Name)), group.Key);
                    foreach (var candidate in group) NoteSteelCandidate(candidate);
                }
            }
            foreach (var t in unverified.Where(t => selected.Contains(t) && !matches.Contains(t)))
                Note(t, "鉄骨候補と位置が重なりますが、同じ高さの柱側面で包囲を確認できません。");
        }

        public Dictionary<Target, Target> ColumnTagOwners(List<Target> columns)
        {
            var owners = new Dictionary<Target, Target>();
            var index = columns.ToDictionary(t => (t.LinkId, t.Element.Id.Value));
            foreach (var t in columns)
                if (t.Element is FamilyInstance f && f.SuperComponent is Element parent &&
                    index.TryGetValue((t.LinkId, parent.Id.Value), out var owner))
                    owners.Add(t, owner);
            var topLevel = columns.Where(t => !owners.ContainsKey(t)).ToList();
            var verified = FindColumnPairs(topLevel, new HashSet<Target>(topLevel), owners, new HashSet<Target>());
            ColumnPair.AddTagOwners(verified, owners, Tolerance);
            return owners;
        }

        private List<ColumnPair> FindColumnPairs(List<Target> context, HashSet<Target> selected,
            Dictionary<Target, Target> copies, HashSet<Target> unverified)
        {
            var shapes = new List<(Target Target, StructuralMaterialType Material, bool Src, ColumnBounds Bounds)>();
            var placements = new Dictionary<(Document, string), List<Target>>();
            foreach (var t in context.OrderByDescending(selected.Contains))
            {
                if (!(t.Element is FamilyInstance f)) continue;
                try
                {
                    var identity = (t.Element.Document, t.Element.UniqueId);
                    if (!placements.TryGetValue(identity, out var positions))
                        placements.Add(identity, positions = new List<Target>());
                    var original = positions.FirstOrDefault(placement => placement.Transform.AlmostEqual(t.Transform));
                    if (original != null)
                    {
                        copies.Add(t, original);
                        continue;
                    }
                    var bounds = GetColumnBounds(t);
                    if (!bounds.HasValue) continue;
                    shapes.Add((t, ColumnMaterial(f), NamedSrc(f), bounds.Value));
                    positions.Add(t);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { if (selected.Contains(t)) Note(t, ex.Message); }
            }
            var steels = shapes.Where(c => !c.Src && c.Material == StructuralMaterialType.Steel).ToList();
            var concrete = shapes.Where(c => c.Src || c.Material == StructuralMaterialType.Concrete ||
                c.Material == StructuralMaterialType.PrecastConcrete || c.Material == StructuralMaterialType.Undefined ||
                c.Material == StructuralMaterialType.Generic || c.Material == StructuralMaterialType.Other).ToList();
            // ponytail: use bounds for the shortlist. Index them if a large model makes this scan expensive.
            var verified = new List<ColumnPair>();
            foreach (var c in concrete)
            foreach (var s in steels)
            {
                var section = c.Bounds.CommonSection(s.Bounds, planCut, Tolerance);
                if (!section.HasValue) continue;
                try
                {
                    var (elevation, atCut, cutDistance) = section.Value;
                    var centerOffset = EnclosedSteelCenterOffset(c.Target, s.Target, elevation);
                    if (!centerOffset.HasValue) { unverified.Add(c.Target); continue; }
                    bool isSubcomponent = c.Target.LinkId == s.Target.LinkId &&
                        ((FamilyInstance)s.Target.Element).SuperComponent?.Id == c.Target.Element.Id;
                    verified.Add(new ColumnPair(c.Target, s.Target, elevation, atCut, cutDistance,
                        centerOffset.Value, isSubcomponent));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { if (selected.Contains(c.Target)) Note(c.Target, ex.Message); }
            }
            return verified;
        }

        private double? EnclosedSteelCenterOffset(Target concrete, Target steel, double elevation)
        {
            var outer = GetGeometry(concrete);
            var inner = GetGeometry(steel);
            var sx = ColumnFaces(inner, x, null, elevation);
            var sy = ColumnFaces(inner, y, null, elevation);
            if (sx.Count < 2 || sy.Count < 2) return null;
            double? CenterOffset(XYZ direction, XYZ along, List<FaceTrace> steelSides, List<FaceTrace> orthogonalSides)
            {
                double low = steelSides[0].Points[0].DotProduct(direction);
                double high = steelSides.Last().Points[0].DotProduct(direction);
                double station = (orthogonalSides[0].Points[0] + orthogonalSides.Last().Points[0]).DotProduct(along) / 2;
                var sideFaces = ColumnFaces(outer, direction, false, elevation).Where(f =>
                    f.Points.Min(p => p.DotProduct(along)) <= station + Tolerance &&
                    f.Points.Max(p => p.DotProduct(along)) >= station - Tolerance).ToList();
                if (sideFaces.Count < 2) return null;
                double outerLow = sideFaces[0].Points[0].DotProduct(direction);
                double outerHigh = sideFaces.Last().Points[0].DotProduct(direction);
                return ColumnBounds.ContainsSpan(outerLow, outerHigh, low, high, Tolerance)
                    ? (outerLow + outerHigh - low - high) / 2 : (double?)null;
            }
            var dx = CenterOffset(x, y, sx, sy);
            var dy = CenterOffset(y, x, sy, sx);
            return dx.HasValue && dy.HasValue ? Math.Sqrt(dx.Value * dx.Value + dy.Value * dy.Value) : (double?)null;
        }

        private static List<FaceTrace> ColumnFaces(ModelGeometry geometry, XYZ direction, bool? steel, double? elevation = null) =>
            geometry.Faces.Where(f => (!steel.HasValue || f.Steel == steel.Value) && Math.Abs(f.Normal.DotProduct(direction)) > Parallel &&
                (!elevation.HasValue || (f.Points.Any(p => p.Z <= elevation.Value + Tolerance) &&
                    f.Points.Any(p => p.Z >= elevation.Value - Tolerance))))
                .OrderBy(f => f.Points[0].DotProduct(direction)).ToList();

        private static (Witness Low, Witness High) ColumnEnds(ModelGeometry geometry, List<FaceTrace> faces, XYZ direction)
        {
            if (faces.Count < 2) return default;
            Witness End(FaceTrace face)
            {
                var end = FaceWitness(face, direction);
                var matching = faces.Where(f => Math.Abs(f.Points[0].DotProduct(direction) - end.Coordinate) <= Tolerance).ToList();
                var referenced = matching.FirstOrDefault(f => f.Reference != null);
                if (referenced != null) return FaceWitness(referenced, direction);
                foreach (var point in matching.SelectMany(f => f.Points))
                {
                    var corner = geometry.AtPoint(point, direction);
                    if (corner != null) return corner;
                }
                return end;
            }
            return (End(faces[0]), End(faces[faces.Count - 1]));
        }

        private View LinkedColumnPlan(Target target)
        {
            if (linkedColumnViews.TryGetValue(target.Link.Id, out var plan)) return plan;
            var doc = target.Element.Document;
            var settings = Target.ReadLinkOverrides(hostView, target.Link);
            if (settings?.LinkVisibilityType == LinkVisibility.ByLinkView && settings.LinkedViewId != null &&
                settings.LinkedViewId != ElementId.InvalidElementId)
                plan = doc.GetElement(settings.LinkedViewId) as ViewPlan;
            if (plan == null && hostView.GenLevel != null)
            {
                double elevation = hostView.GenLevel.ProjectElevation;
                plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v =>
                        !v.IsTemplate && v.GenLevel != null && (v.ViewType == ViewType.EngineeringPlan || v.ViewType == ViewType.FloorPlan) &&
                        Math.Abs(target.Transform.OfPoint(new XYZ(0, 0, v.GenLevel.ProjectElevation)).Z - elevation) <= Tolerance)
                    .OrderByDescending(v => v.Name == hostView.Name).ThenByDescending(v => v.DetailLevel == hostView.DetailLevel)
                    .ThenByDescending(v => v.ViewType == hostView.ViewType).ThenBy(v => v.Id.Value).FirstOrDefault();
            }
            linkedColumnViews.Add(target.Link.Id, plan);
            return plan;
        }

        private ColumnPlanOutline FindColumnPlanOutline(Target target, ModelGeometry geometry)
        {
            var (left, right) = ColumnEnds(geometry, ColumnFaces(geometry, x, false), x);
            var (bottom, top) = ColumnEnds(geometry, ColumnFaces(geometry, y, false), y);
            if (left == null || right == null || bottom == null || top == null) return null;
            IEnumerable<EdgeTrace> edges = geometry.ColumnPlanLines;
            try
            {
                var view = LinkedColumnPlan(target);
                if (view != null)
                {
                    var planGeometry = new ModelGeometry(target, geometryView: view);
                    edges = edges.Concat(planGeometry.ColumnPlanLines).Concat(planGeometry.Edges);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            { Note(target, "リンク平面の柱外周を確認できませんでした：" + ex.Message); }
            return ColumnPlanOutline.Find(edges, x, y, left.Coordinate, right.Coordinate, bottom.Coordinate, top.Coordinate);
        }

        private List<FaceTrace> ColumnFukashi(Target target)
        {
            var result = new List<FaceTrace>();
            if (!(target.Element is FamilyInstance column)) return result;
            foreach (var id in column.GetSubComponentIds())
            {
                if (!(column.Document.GetElement(id) is FamilyInstance part) || !NamedFukashi(part)) continue;
                try
                {
                    var geometry = new ModelGeometry(new Target(target.Link, part), includeNonVisible: true);
                    result.AddRange(geometry.Faces.Where(face => !face.Steel));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                { Note(target, "ふかし部材の参照を取得できませんでした：" + ex.Message); }
            }
            return result;
        }

        private static (Witness Low, Witness High) FukashiEnds(List<FaceTrace> faces, XYZ direction, Witness bodyLow, Witness bodyHigh,
            double? lowThickness, double? highThickness)
        {
            if (faces.Count == 0 || bodyLow?.Reference == null || bodyHigh?.Reference == null) return default;
            var positions = Positions(faces.Where(f => f.Reference != null && Math.Abs(f.Normal.DotProduct(direction)) > Parallel), direction);
            Witness Outside(Witness body, double? thickness, int side)
            {
                if (thickness.HasValue && thickness.Value <= Tolerance) return body;
                if (thickness.HasValue)
                {
                    double expected = body.Coordinate + side * thickness.Value;
                    return positions.FirstOrDefault(w => Math.Abs(w.Coordinate - expected) <= Tolerance);
                }
                return positions.Where(w => (w.Coordinate - body.Coordinate) * side > Tolerance)
                    .OrderByDescending(w => w.Coordinate * side).FirstOrDefault() ?? body;
            }
            return (Outside(bodyLow, lowThickness, -1), Outside(bodyHigh, highThickness, 1));
        }

        private void Column(Target t, FamilyInstance f)
        {
            bool src = NamedSrc(f);
            if (ColumnMaterial(f) == StructuralMaterialType.Steel && !src) return;
            columnSteel.TryGetValue(t, out var pairing);
            var g = GetGeometry(t);
            var fukashi = ColumnFukashi(t);
            if (pairing.Steel != null)
            {
                try { LinkedSteelColumn(pairing.Steel, g, pairing.Elevation, fukashi); }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Note(pairing.Steel, ex.Message); }
            }
            src = src || (g.Faces.Any(face => face.Steel) && g.Faces.Any(face => !face.Steel));
            var detailed = new Lazy<ModelGeometry>(() => new ModelGeometry(t, includeNonVisible: true));
            var planOutline = new Lazy<ColumnPlanOutline>(() => FindColumnPlanOutline(t, g));
            var points = g.Faces.Where(p => !p.Steel).Concat(fukashi).SelectMany(p => p.Points).ToList();
            if (points.Count == 0 || g.HasNonPlanarSide) { Note(t, "柱のコンクリート外形を矩形として認識できませんでした。"); return; }
            var transform = t.Transform.Multiply(f.GetTransform());
            var localX = Flat(transform.BasisX).Normalize();
            if (Math.Max(Math.Abs(localX.DotProduct(x)), Math.Abs(localX.DotProduct(y))) < Parallel)
            { Note(t, "通り芯に対して斜めの柱です。寸法を確認してください。"); return; }
            foreach (var (direction, along) in new[] { (x, y), (y, x) })
            {
                double start = points.Min(p => p.DotProduct(along)), end = points.Max(p => p.DotProduct(along));
                bool depth = Math.Abs(localX.DotProduct(direction)) < Parallel;
                var width = map.Length(f, depth ? "BodyDepth" : "BodyWidth") ??
                    Length(f, depth ? BuiltInParameter.STRUCTURAL_SECTION_COMMON_HEIGHT : BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH) ??
                    (depth ? null : Length(f, BuiltInParameter.FAMILY_WIDTH_PARAM));
                var addLow = map.Length(f, depth ? "DepthLow" : "SideLow");
                var addHigh = map.Length(f, depth ? "DepthHigh" : "SideHigh");
                if (Flat(depth ? transform.BasisY : transform.BasisX).DotProduct(direction) < 0)
                    (addLow, addHigh) = (addHigh, addLow);
                bool declaredFukashi = addLow > Tolerance || addHigh > Tolerance;
                var concrete = ColumnFaces(g, direction, false);
                var (lo, hi) = ColumnEnds(g, concrete, direction);
                (Witness low, Witness high) Core(List<FaceTrace> sides, Witness a, Witness b) => a == null || b == null
                    ? (null, null) : Body(t, sides.Where(face => face.Reference != null).ToList(), direction, a, b, width, depth, true,
                        new[] { a, b }.Where(w => w.Reference != null));
                var (bodyLow, bodyHigh) = Core(concrete, lo, hi);
                bool added = Added(lo, hi, bodyLow, bodyHigh);
                if (!added && fukashi.Count > 0 && lo != null && hi != null &&
                    (!width.HasValue || Math.Abs(hi.Coordinate - lo.Coordinate - width.Value) <= Tolerance))
                {
                    var (fkLow, fkHigh) = FukashiEnds(fukashi, direction, lo, hi, addLow, addHigh);
                    if (fkLow != null && fkHigh != null && Added(fkLow, fkHigh, lo, hi))
                    {
                        (lo, hi, bodyLow, bodyHigh) = (fkLow, fkHigh, lo, hi);
                        added = true;
                    }
                }
                if ((!added && (src || pairing.Steel != null || declaredFukashi)) || lo?.Reference == null || hi?.Reference == null)
                {
                    var moreFaces = ColumnFaces(detailed.Value, direction, false);
                    var (outerLow, outerHigh) = ColumnEnds(detailed.Value, moreFaces, direction);
                    if (outerLow?.Reference != null && outerHigh?.Reference != null &&
                        (lo == null || outerLow.Coordinate <= lo.Coordinate + Tolerance) &&
                        (hi == null || outerHigh.Coordinate >= hi.Coordinate - Tolerance))
                    {
                        var (coreLow, coreHigh) = Core(moreFaces, outerLow, outerHigh);
                        bool visibleCoreMatches = width.HasValue && Same(lo, coreLow) && Same(hi, coreHigh);
                        bool evidencedFukashi = Added(outerLow, outerHigh, coreLow, coreHigh) &&
                            (declaredFukashi || visibleCoreMatches || moreFaces.Any(face => face.Added));
                        if (evidencedFukashi || (Same(lo, outerLow) && Same(hi, outerHigh) &&
                            (lo.Reference == null || hi.Reference == null)))
                            (lo, hi, bodyLow, bodyHigh) = (outerLow, outerHigh, coreLow, coreHigh);
                        added = Added(lo, hi, bodyLow, bodyHigh);
                    }
                }
                if (lo == null || hi == null || hi.Coordinate - lo.Coordinate <= Tolerance)
                { Note(t, "柱のコンクリート外面で、平行な一対の面が見つかりません。"); continue; }
                if (!added && planOutline.Value is ColumnPlanOutline outline)
                {
                    var outerLow = direction == x ? outline.Left : outline.Bottom;
                    var outerHigh = direction == x ? outline.Right : outline.Top;
                    if (outerLow.Coordinate <= lo.Coordinate + Tolerance && outerHigh.Coordinate >= hi.Coordinate - Tolerance &&
                        Added(outerLow, outerHigh, lo, hi))
                    {
                        var (coreLow, coreHigh) = Core(concrete, outerLow, outerHigh);
                        if (coreLow == null && !declaredFukashi) (coreLow, coreHigh) = (lo, hi);
                        if (coreLow?.Reference != null && coreHigh?.Reference != null && Added(outerLow, outerHigh, coreLow, coreHigh))
                        {
                            if (Same(outerLow, lo)) outerLow = lo;
                            if (Same(outerHigh, hi)) outerHigh = hi;
                            (lo, hi, bodyLow, bodyHigh) = (outerLow, outerHigh, coreLow, coreHigh);
                            added = true;
                        }
                        else Note(t, "平面外周を検出しましたが、躯体とふかしの参照を確定できません。");
                    }
                }
                var grid = NearestGrid(direction, (lo.Coordinate + hi.Coordinate) / 2);
                bool inside = Between(grid, lo, hi);
                bool placeBelow = inside && (src || pairing.Steel != null) && direction == x;
                double side = placeBelow || (!inside && grid != null && grid.Coordinate > hi.Coordinate) ? -1 : 1;
                double near = ColumnStation(start, end, side, columnOffset);
                double far = near + side * columnRowGap;
                if (!declaredFukashi || added)
                    Add(t, "柱の外面から通り芯までの寸法", direction, along, added ? far : near, false, new[] { lo, grid, hi });
                if (added) Add(t, "柱の躯体・ふかし寸法", direction, along, near, false, new[] { lo, bodyLow, bodyHigh, hi });
                else if (declaredFukashi || bodyLow == null) Note(t, "柱の躯体面とふかし面を区別できませんでした。");
                if (!src || pairing.Steel != null) continue;
                List<FaceTrace> InsideSteel(ModelGeometry model) => ColumnFaces(model, direction, true).Where(face =>
                    face.Points[0].DotProduct(direction) > lo.Coordinate + Tolerance &&
                    face.Points[0].DotProduct(direction) < hi.Coordinate - Tolerance).ToList();
                var (sl, sh) = ColumnEnds(g, InsideSteel(g), direction);
                if (sl?.Reference == null || sh?.Reference == null)
                    (sl, sh) = ColumnEnds(detailed.Value, InsideSteel(detailed.Value), direction);
                double steelNear = ColumnStation(start, end, -side, columnOffset);
                AddSteelColumnRows(t, direction, along, steelNear, -side, sl, sh);
            }
        }

        private void LinkedSteelColumn(Target steel, ModelGeometry concrete, double elevation, List<FaceTrace> fukashi)
        {
            var g = GetGeometry(steel);
            var detailed = new Lazy<ModelGeometry>(() => new ModelGeometry(steel, includeNonVisible: true));
            foreach (var (direction, along) in new[] { (x, y), (y, x) })
            {
                var (low, high) = ColumnEnds(g, ColumnFaces(g, direction, null, elevation), direction);
                if (low?.Reference == null || high?.Reference == null)
                {
                    var (a, b) = ColumnEnds(detailed.Value, ColumnFaces(detailed.Value, direction, null, elevation), direction);
                    if (Same(low, a) && Same(high, b)) (low, high) = (a, b);
                }
                var alongFaces = ColumnFaces(concrete, along, false, elevation);
                if (alongFaces.Count < 2) continue;
                double side = direction == x ? 1 : -1;
                var points = alongFaces.Concat(fukashi).SelectMany(face => face.Points).ToList();
                double start = points.Min(p => p.DotProduct(along));
                double end = points.Max(p => p.DotProduct(along));
                AddSteelColumnRows(steel, direction, along, ColumnStation(start, end, side, columnOffset), side, low, high);
            }
        }

        private void AddSteelColumnRows(Target steel, XYZ direction, XYZ along, double station, double side,
            Witness low, Witness high)
        {
            if (low?.Reference == null || high?.Reference == null || high.Coordinate - low.Coordinate <= Tolerance)
            { Note(steel, "SRC 柱の内蔵鉄骨で、外面の寸法参照が見つかりません。"); return; }
            var grid = NearestGrid(direction, (low.Coordinate + high.Coordinate) / 2);
            Add(steel, "SRC 柱の内蔵鉄骨位置", direction, along, station, false, new[] { low, grid, high });
            Add(steel, "SRC 柱の内蔵鉄骨全幅", direction, along, station + side * columnRowGap, false, new[] { low, high });
        }

        private static bool Between(Witness w, Witness a, Witness b) =>
            w != null && w.Coordinate > a.Coordinate + Tolerance && w.Coordinate < b.Coordinate - Tolerance;
        private static bool Added(Witness a, Witness b, Witness c, Witness d) =>
            c != null && d != null && (c.Coordinate - a.Coordinate > Tolerance || b.Coordinate - d.Coordinate > Tolerance);
        private static double ColumnStation(double start, double end, double side, double distance) =>
            side > 0 ? end + distance : start - distance;

        private double RowGap => Math.Max(offset * 0.6, Mm(300));

        private (Witness Low, Witness High, Witness BodyLow, Witness BodyHigh, Witness Center) BeamSection(
            Target t, FamilyInstance f, ModelGeometry g, XYZ direction, XYZ along, double station)
        {
            var width = map.Length(f, "BodyWidth") ?? Length(f, BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH) ??
                Length(f, BuiltInParameter.FAMILY_WIDTH_PARAM);
            bool encased = Contains(Target.TypeName(f), "SRC", "ＳＲＣ", "鉄骨鉄筋", "型钢混凝土");
            var raw = SideFaces(g, direction, along, station, false);
            bool steelOnly = !encased && (f.StructuralMaterialType == Autodesk.Revit.DB.Structure.StructuralMaterialType.Steel ||
                (raw.Count > 0 && raw.All(face => face.Steel)));
            var faces = raw.Where(face => steelOnly || !face.Steel).ToList();
            var measured = faces.Select(face => FaceWitness(face, direction)).OrderBy(w => w.Coordinate).ToList();
            var referenced = faces.Where(face => face.Reference != null).ToList();
            var edges = g.Edges.Where(e =>
                    Math.Abs(e.A.Z - e.B.Z) <= Tolerance &&
                    ModelGeometry.PlanDistance(e.A, e.B) > Tolerance &&
                    Math.Abs(Flat(e.B - e.A).Normalize().DotProduct(along)) > Parallel &&
                    Math.Min(e.A.DotProduct(along), e.B.DotProduct(along)) - Tolerance <= station &&
                    Math.Max(e.A.DotProduct(along), e.B.DotProduct(along)) + Tolerance >= station)
                .Select(e => new Witness(e.Edge, e.A.DotProduct(direction))).ToList();

            if (measured.Count < 2 || measured.Last().Coordinate - measured[0].Coordinate <= Tolerance)
            {
                if (encased && raw.Any(face => face.Steel)) return default;
                measured.AddRange(edges);
                measured = measured.OrderBy(w => w.Coordinate).ToList();
            }
            if ((measured.Count < 2 || measured.Last().Coordinate - measured[0].Coordinate <= Tolerance) && width.HasValue)
            {
                var bounds = BoxPoints(f.get_BoundingBox(null)).Select(t.Transform.OfPoint).ToList();
                double expectedWidth = width.Value + (steelOnly ? 0 :
                    (map.Length(f, "SideLow") ?? 0) + (map.Length(f, "SideHigh") ?? 0));
                if (bounds.Count > 0 && bounds.Min(p => p.DotProduct(along)) <= station &&
                    bounds.Max(p => p.DotProduct(along)) >= station)
                {
                    double loCoordinate = bounds.Min(p => p.DotProduct(direction));
                    double hiCoordinate = bounds.Max(p => p.DotProduct(direction));
                    if (Math.Abs(hiCoordinate - loCoordinate - expectedWidth) <= Tolerance)
                        measured = new List<Witness> { new Witness(null, loCoordinate), new Witness(null, hiCoordinate) };
                }
            }
            if (measured.Count < 2 || measured.Last().Coordinate - measured[0].Coordinate <= Tolerance)
                return default;

            var form = t.Transform.Multiply(f.GetTransform());
            double lateral = Flat(form.BasisY).Normalize().DotProduct(direction);
            var lowRole = lateral >= 0 ? FamilyInstanceReferenceType.Front : FamilyInstanceReferenceType.Back;
            var highRole = lateral >= 0 ? FamilyInstanceReferenceType.Back : FamilyInstanceReferenceType.Front;
            Witness Resolve(double c, FamilyInstanceReferenceType role)
            {
                var direct = measured.Concat(edges).FirstOrDefault(w => w.Reference != null &&
                    Math.Abs(w.Coordinate - c) <= Tolerance);
                return direct ?? (Math.Abs(lateral) > Parallel ? FamilyPlane(t, f, role, c) : null);
            }
            var lo = Resolve(measured[0].Coordinate, lowRole);
            var hi = Resolve(measured.Last().Coordinate, highRole);
            if (lo == null || hi == null) return default;
            var core = steelOnly ? (low: lo, high: hi) :
                Body(t, referenced, direction, lo, hi, width, false, true, new[] { lo, hi });
            Witness center = null;
            if (core.low != null)
            {
                double expected = (core.low.Coordinate + core.high.Coordinate) / 2;
                center = Named(t, f, "Core Center", expected) ?? CenterPlane(t, f, direction, expected);
                if (center == null && f.Location is LocationCurve axis && axis.Curve is Line axisLine &&
                    Math.Abs(t.Transform.OfPoint(axisLine.GetEndPoint(0)).DotProduct(direction) - expected) <= Tolerance &&
                    axisLine.Reference != null)
                    center = new Witness(t.InHost(axisLine.Reference), expected);
            }
            return (lo, hi, core.low, core.high, center);
        }

        private void Beam(Target t, FamilyInstance f)
        {
            if (!(f.Location is LocationCurve loc) || !(loc.Curve is Line line))
            { Note(t, "曲線梁はこのバージョンでは対象外です。"); return; }
            var tangent = Flat(t.Transform.OfVector(line.Direction)).Normalize();
            if (Math.Max(Math.Abs(tangent.DotProduct(x)), Math.Abs(tangent.DotProduct(y))) < Parallel ||
                Math.Abs(t.Transform.OfVector(line.Direction).Z) > 0.00001)
            { Note(t, "斜めの梁や勾配のある梁は、このバージョンでは対象外です。"); return; }
            var along = Math.Abs(tangent.DotProduct(x)) > Parallel ? x : y;
            var direction = along == x ? y : x;
            double mid = t.Transform.OfPoint(line.Evaluate(0.5, true)).DotProduct(along);
            var (lo, hi, a, b, center) = BeamSection(t, f, GetGeometry(t), direction, along, mid);
            if (lo == null || a == null)
            {
                var alternate = BeamSection(t, f, new ModelGeometry(t, includeNonVisible: true), direction, along, mid);
                if (alternate.Low != null && (lo == null ||
                    (alternate.BodyLow != null && Same(lo, alternate.Low) && Same(hi, alternate.High))))
                    (lo, hi, a, b, center) = alternate;
            }
            if (lo == null)
            { Note(t, "梁中央で、寸法に使える側面・エッジ・ファミリ参照が見つかりません。"); return; }
            bool added = Added(lo, hi, a, b);
            var grid = NearestGrid(direction, (lo.Coordinate + hi.Coordinate) / 2);
            DimensionPlan singleLocation = null;
            if (grid == null) Note(t, "現在のモデルに、基準として使える平行な通り芯がありません。");
            else if (a != null && center != null && Math.Abs(grid.Coordinate - center.Coordinate) > Tolerance)
                singleLocation = Add(t, "梁芯の位置寸法", direction, along, mid, true, new[] { grid, center });
            else if (center == null)
                Note(t, "梁の位置寸法に必要な梁芯参照が見つかりません。");

            if (added)
                Add(t, "梁の躯体・ふかし寸法", direction, along, mid - RowGap, true, new[] { lo, a, b, hi });

            if (beamWidth != BeamWidthMode.Omit)
            {
                var widthCenter = WidthCenter(grid, a, b, center);
                var marks = beamWidth == BeamWidthMode.Split ? new[] { lo, widthCenter, hi } : new[] { lo, hi };
                double station = singleLocation == null ? mid : mid + RowGap;
                var plan = Add(t, beamWidth == BeamWidthMode.Split ? "梁芯で分割した梁幅" : "梁全幅",
                    direction, along, station, true, marks);
                if (plan != null) beamWidthPlans[t] = plan;
            }
            if (a == null) Note(t, "梁の躯体幅またはふかし面を特定できません。ファミリ参照を確認してください。");
            if (center != null)
            {
                double start = t.Transform.OfPoint(line.GetEndPoint(0)).DotProduct(along);
                double end = t.Transform.OfPoint(line.GetEndPoint(1)).DotProduct(along);
                beamPositions.Add((t, along, direction, Math.Min(start, end), Math.Max(start, end), center, singleLocation));
            }
        }

        private static Witness WidthCenter(Witness grid, Witness bodyLow, Witness bodyHigh, Witness center)
        {
            if (grid != null && bodyLow != null && bodyHigh != null &&
                Math.Abs(grid.Coordinate - (bodyLow.Coordinate + bodyHigh.Coordinate) / 2) <= Tolerance)
                return grid;
            return center;
        }

        private void BeamChains(int insertAt)
        {
            foreach (var byDirection in beamPositions.GroupBy(p => p.Along))
            {
                var set = byDirection.OrderBy(p => p.Target.Link.Id.Value).ThenBy(p => p.Target.Element.Id.Value).ToList();
                foreach (var (low, high, indices) in BeamChainSections.Find(set.Select(p => (p.Low, p.High)).ToList(), Tolerance))
                {
                    var members = indices.Select(i => set[i]).OrderBy(p => p.Center.Coordinate).ToList();
                    var first = members.First();
                    var last = members.Last();
                    double from = first.Center.Coordinate, to = last.Center.Coordinate;
                    if (to - from <= Tolerance) continue;
                    var gridsForChain = Grids(first.Direction).ToList();
                    var anchor = gridsForChain.Where(g => g.Coordinate < from - Tolerance || g.Coordinate > to + Tolerance)
                        .OrderBy(g => Math.Min(Math.Abs(g.Coordinate - from), Math.Abs(g.Coordinate - to)))
                        .FirstOrDefault() ?? gridsForChain.FirstOrDefault(g =>
                            Math.Abs(g.Coordinate - from) <= Tolerance || Math.Abs(g.Coordinate - to) <= Tolerance);
                    var references = members.Select(p => p.Center);
                    if (anchor != null) references = references.Append(anchor);
                    var plan = Add(first.Target, "梁芯の連続寸法", first.Direction, first.Along,
                        (low + high) / 2, true, references);
                    if (plan == null) continue;
                    plan.BeamChainRange = (low, high);
                    plan.ChainBeams = members.Select(p => p.Target).ToList();
                    if (anchor != null)
                        foreach (var member in members.Where(p => p.SingleLocation != null))
                            member.SingleLocation.ReplacedBy = plan;
                    plans.RemoveAt(plans.Count - 1);
                    plans.Insert(insertAt++, plan);
                }
            }
        }

        private void WallDimensions(Target t, Wall wall)
        {
            if (!(wall.Location is LocationCurve loc) || !(loc.Curve is Line line))
            { Note(t, "曲面壁の寸法を確認してください。"); return; }
            var start = t.Transform.OfPoint(line.GetEndPoint(0));
            var end = t.Transform.OfPoint(line.GetEndPoint(1));
            var tangent = Flat(end - start).Normalize();
            if (tangent.GetLength() < 0.5) { Note(t, "壁の平面上の長さを取得できません。"); return; }
            var g = GetGeometry(t);
            if (Math.Max(Math.Abs(tangent.DotProduct(x)), Math.Abs(tangent.DotProduct(y))) < Parallel)
            {
                InclinedWall(t, g, start, end, tangent);
                return;
            }
            XYZ along = Math.Abs(tangent.DotProduct(x)) > Parallel ? x : y;
            XYZ direction = along == x ? y : x;
            var allSides = g.Faces.Where(f => f.Reference != null && Math.Abs(f.Normal.DotProduct(direction)) > Parallel).ToList();
            var boundaries = UniqueCoordinates(allSides.SelectMany(f => new[]
                { f.Points.Min(p => p.DotProduct(along)), f.Points.Max(p => p.DotProduct(along)) }));
            double width = map.Length(wall, "WallThickness") ?? wall.Width;
            var segments = new List<(double a, double b, Witness lo, Witness hi, Witness bodyLo, Witness bodyHi)>();
            for (int i = 1; i < boundaries.Count; i++)
            {
                double a = boundaries[i - 1], b = boundaries[i];
                var faces = SideFaces(g, direction, along, (a + b) / 2);
                var positions = Positions(faces, direction);
                if (positions.Count < 2) continue;
                var lo = positions.First(); var hi = positions.Last();
                var body = Body(t, faces, direction, lo, hi, width, false, false);
                if (segments.Count > 0)
                {
                    var last = segments.Last();
                    if (Math.Abs(a - last.b) < Tolerance && Same(lo, last.lo) && Same(hi, last.hi) &&
                        Same(body.low, last.bodyLo) && Same(body.high, last.bodyHi))
                    {
                        segments[segments.Count - 1] = (last.a, b, lo, hi, body.low, body.high);
                        continue;
                    }
                }
                segments.Add((a, b, lo, hi, body.low, body.high));
            }
            if (segments.Count == 0) { Note(t, "寸法記入できる壁の側面が不足しています。"); return; }
            var steps = new List<Witness>();
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                var grid = NearestGrid(direction, (s.lo.Coordinate + s.hi.Coordinate) / 2);
                double station = (s.a + s.b) / 2;
                bool hasAdded = Added(s.lo, s.hi, s.bodyLo, s.bodyHi);
                if (s.bodyLo == null) Note(t, "壁の躯体厚またはふかし面を特定できません。");
                if (Between(grid, s.lo, s.hi))
                {
                    Add(t, "壁の位置寸法", direction, along, station, false, new[] { s.lo, grid, s.hi });
                    if (hasAdded) Add(t, "壁の躯体・ふかし寸法", direction, along, station - RowGap, false,
                        new[] { s.lo, s.bodyLo, s.bodyHi, s.hi });
                }
                else
                    Add(t, "壁の位置寸法及厚度", direction, along, station, false,
                        hasAdded ? new[] { grid, s.lo, s.bodyLo, s.bodyHi, s.hi } : new[] { grid, s.lo, s.hi });
                if (i == 0) continue;
                var prev = segments[i - 1];
                if (Math.Abs(s.a - prev.b) > Tolerance || (Same(s.lo, prev.lo) && Same(s.hi, prev.hi))) continue;
                var endFace = g.Faces.Where(f => f.Reference != null && Math.Abs(f.Normal.DotProduct(along)) > Parallel &&
                        Math.Abs(f.Points[0].DotProduct(along) - s.a) <= Tolerance)
                    .FirstOrDefault(f => Center(f, direction) <= Math.Min(s.lo.Coordinate, prev.lo.Coordinate) + Math.Abs(s.lo.Coordinate - prev.lo.Coordinate) + Tolerance ||
                        Center(f, direction) >= Math.Max(s.hi.Coordinate, prev.hi.Coordinate) - Math.Abs(s.hi.Coordinate - prev.hi.Coordinate) - Tolerance);
                if (endFace == null) Note(t, "ふかしの切替位置に寸法記入できる端面が見つかりません。");
                else steps.Add(FaceWitness(endFace, along));
            }

            var first = segments.First(); var lastSegment = segments.Last();
            double dimensionStation = segments.Max(s => s.hi.Coordinate) + offset;
            var ends = new List<Witness>();
            for (int i = 0; i < 2; i++)
            {
                if (loc.get_ElementsAtJoin(i).Size > 0) continue;
                double endpoint = (i == 0 ? start : end).DotProduct(along);
                double outer = Math.Abs(endpoint - first.a) < Math.Abs(endpoint - lastSegment.b) ? first.a : lastSegment.b;
                var endFace = g.Faces.FirstOrDefault(f => f.Reference != null && Math.Abs(f.Normal.DotProduct(along)) > Parallel &&
                    Math.Abs(f.Points[0].DotProduct(along) - outer) <= Tolerance);
                if (endFace == null) { Note(t, "壁の自由端に寸法記入できる端面がありません。"); continue; }
                ends.Add(FaceWitness(endFace, along));
            }
            if (steps.Count > 0)
            {
                var seed = NearestGrid(along, steps[0].Coordinate);
                if (seed == null) Note(t, "ふかしの切替位置を壁の長手方向で測るための通り芯が見つかりません。");
                else
                {
                    var nearEnds = ends.Where(e => steps.Min(s => Math.Abs(s.Coordinate - e.Coordinate)) <= Mm(5000)).ToList();
                    Add(t, "壁のふかし切替位置", along, direction, dimensionStation, false, steps.Concat(nearEnds).Append(seed));
                    ends.RemoveAll(e => nearEnds.Contains(e));
                }
            }
            int lane = 0;
            foreach (var ending in ends)
            {
                var available = Grids(along).ToList();
                var before = available.Where(w => w.Coordinate < ending.Coordinate - Tolerance).OrderByDescending(w => w.Coordinate).FirstOrDefault();
                var after = available.Where(w => w.Coordinate > ending.Coordinate + Tolerance).OrderBy(w => w.Coordinate).FirstOrDefault();
                if (available.Any(w => Math.Abs(w.Coordinate - ending.Coordinate) <= Tolerance)) continue;
                if (before == null && after == null) { Note(t, "壁端部の位置を測るための通り芯がありません。"); continue; }
                Add(t, "壁端部の位置寸法", along, direction, dimensionStation + lane++ * RowGap, false,
                    new[] { before, ending, after }.Where(w => w != null));
            }
        }

        private static bool Same(Witness a, Witness b) =>
            a == null || b == null ? a == b : Math.Abs(a.Coordinate - b.Coordinate) <= Tolerance;

        private static List<double> UniqueCoordinates(IEnumerable<double> positions) =>
            positions.OrderBy(p => p).Aggregate(new List<double>(), (list, p) =>
            {
                if (list.Count == 0 || p - list.Last() > Tolerance) list.Add(p);
                return list;
            });

        private void InclinedWall(Target t, ModelGeometry g, XYZ start, XYZ end, XYZ tangent)
        {
            foreach (XYZ endpoint in new[] { start, end })
            {
                var horizontal = NearestGrid(y, endpoint.DotProduct(y));
                if (horizontal == null) { Note(t, "斜め壁の端部を測るための横方向の通り芯がありません。"); continue; }
                var cross = XYZ.BasisZ.CrossProduct(tangent);
                var endFace = g.Faces.Where(f => Math.Abs(f.Normal.DotProduct(tangent)) > Parallel)
                    .OrderBy(f => Math.Abs(Center(f, tangent) - endpoint.DotProduct(tangent)))
                    .ThenByDescending(f => f.Points.Max(p => p.DotProduct(cross)) - f.Points.Min(p => p.DotProduct(cross)))
                    .FirstOrDefault();
                if (endFace == null) { Note(t, "斜め壁の端面を特定できませんでした。"); continue; }
                var corner = endFace.Points.OrderBy(p => Math.Abs(horizontal.Coordinate - p.DotProduct(y))).First();
                var vertical = NearestGrid(x, corner.DotProduct(x));
                double sideX = (end - start).DotProduct(x) >= 0 ? 1 : -1;
                if (endpoint == start) sideX = -sideX;
                double sideY = horizontal.Coordinate >= corner.DotProduct(y) ? 1 : -1;
                Add(t, "斜め壁端部の横方向位置", x, y, corner.DotProduct(y) + sideY * offset, false,
                    new[] { vertical, g.AtPoint(corner, x) });
                Add(t, "斜め壁端部の縦方向位置", y, x, corner.DotProduct(x) + sideX * offset, false,
                    new[] { horizontal, g.AtPoint(corner, y) });
            }
        }

        private void Hatch(Target t, FamilyInstance f)
        {
            if (!(f.Location is LocationPoint loc)) { Note(t, "床点検口の中心位置を取得できません。"); return; }
            var center = t.Transform.OfPoint(loc.Point);
            foreach (var (direction, along) in new[] { (x, y), (y, x) })
            {
                double c = center.DotProduct(direction);
                Add(t, "床点検口中心の位置寸法", direction, along, center.DotProduct(along) + offset, false,
                    new[] { NearestGrid(direction, c), CenterPlane(t, f, direction, c) });
            }
        }

        private void FloorEdges(Target t, Floor floor)
        {
            foreach (Reference reference in HostObjectUtils.GetTopFaces(floor))
            {
                var face = floor.GetGeometryObjectFromReference(reference) as PlanarFace;
                if (face == null || Math.Abs(t.Transform.OfVector(face.FaceNormal).Z) < Parallel)
                { Note(t, "勾配床または曲面床の端部を確認してください。"); continue; }
                var loops = face.EdgeLoops.Cast<EdgeArray>().OrderByDescending(loop =>
                {
                    var pts = loop.Cast<Edge>().Select(e => e.AsCurveFollowingFace(face).GetEndPoint(0)).ToList();
                    return Math.Abs(pts.Select((p, i) => p.X * pts[(i + 1) % pts.Count].Y -
                        p.Y * pts[(i + 1) % pts.Count].X).Sum());
                }).ToList();
                if (loops.Count == 0) continue;
                bool isElevator = map.Matches("Elevator", Target.TypeName(floor));
                if (isElevator && loops.Count > 1)
                {
                    foreach (var loop in loops.Skip(1)) FloorLoop(t, face, loop, true);
                }
                else FloorLoop(t, face, loops[0], false);
            }
        }

        private void FloorLoop(Target t, PlanarFace face, EdgeArray loop, bool elevator)
        {
            var boundary = loop.Cast<Edge>().Select(edge => (edge, curve: edge.AsCurveFollowingFace(face))).ToList();
            if (boundary.Any(i => !(i.curve is Line))) Note(t, "直線以外の床端部を確認してください。");
            var points = boundary.Select(i => t.Transform.OfPoint(i.curve.GetEndPoint(0))).ToList();
            int n = points.Count;
            double handedness = Math.Sign(points.Select((p, i) =>
                p.X * points[(i + 1) % n].Y - p.Y * points[(i + 1) % n].X).Sum());
            var used = new List<(int index, XYZ direction, XYZ along, Witness edge, Witness grid, double station, bool covered)>();
            for (int i = 0; i < boundary.Count; i++)
            {
                var (edge, curve) = boundary[i];
                if (!(curve is Line line)) continue;
                var a = t.Transform.OfPoint(line.GetEndPoint(0)); var b = t.Transform.OfPoint(line.GetEndPoint(1));
                var tangent = Flat(b - a).Normalize();
                if (Math.Max(Math.Abs(tangent.DotProduct(x)), Math.Abs(tangent.DotProduct(y))) < Parallel)
                { Note(t, "通り芯に対して斜めの床端部を確認してください。"); continue; }
                XYZ along = Math.Abs(tangent.DotProduct(x)) > Parallel ? x : y;
                XYZ direction = along == x ? y : x;
                double coordinate = a.DotProduct(direction);
                double mid = ((a + b) / 2).DotProduct(along);
                var witness = new Witness(t.InHost(edge.Reference), coordinate);
                var grid = NearestGrid(direction, coordinate);
                bool covered = grid != null && plans.Any(p => p.Target != null && p.Target != t &&
                    Category(p.Target.Element) != BuiltInCategory.OST_Floors && Math.Abs(p.Direction.DotProduct(direction)) > Parallel &&
                    p.Marks.Any(w => w.Grid && w.Reference.ElementId == grid.Reference.ElementId) &&
                    p.Marks.Any(w => !w.Grid && Math.Abs(w.Coordinate - coordinate) <= Tolerance) &&
                    CoincidentSpan(p.Target, direction, along, coordinate, a.DotProduct(along), b.DotProduct(along)));
                used.Add((i, direction, along, witness, grid, mid, covered && !elevator));
                if (elevator) ElevatorBeam(t, direction, along, mid, witness);
            }
            var chained = new HashSet<int>();
            if (!elevator && boundary.All(e => e.curve is Line))
            {
                if (handedness != 0)
                {
                    bool Reflex(int i)
                    {
                        var incoming = points[i] - points[(i + n - 1) % n];
                        var outgoing = points[(i + 1) % n] - points[i];
                        return incoming.CrossProduct(outgoing).Z * handedness < -1e-7;
                    }
                    foreach (var first in used.Where(i => !i.covered))
                    {
                        int sideA = (first.index + 1) % n, sideB = (first.index + 2) % n;
                        if (chained.Contains(first.index) || chained.Contains(sideB)) continue;
                        var second = used.FirstOrDefault(i => i.index == sideB && !i.covered && i.direction == first.direction);
                        if (second.edge == null || first.grid == null) continue;
                        var travelA = points[sideA] - points[first.index];
                        var travelB = points[(sideB + 1) % n] - points[sideB];
                        bool notch = Reflex(sideA) && Reflex(sideB);
                        bool step = !notch && Reflex(sideA) != Reflex(sideB) && travelA.DotProduct(travelB) > 0;
                        if (!notch && !step) continue;
                        var common = Grids(first.direction).Where(g =>
                            g.Coordinate <= Math.Min(first.edge.Coordinate, second.edge.Coordinate) + Tolerance ||
                            g.Coordinate >= Math.Max(first.edge.Coordinate, second.edge.Coordinate) - Tolerance)
                            .OrderBy(g => Math.Min(Math.Abs(g.Coordinate - first.edge.Coordinate),
                                Math.Abs(g.Coordinate - second.edge.Coordinate))).FirstOrDefault();
                        if (common == null) continue;
                        int before = plans.Count;
                        Add(t, notch ? "床の切欠き位置" : "床端部の位置・出入り寸法", first.direction, first.along,
                            (first.station + second.station) / 2 + offset, false, new[] { common, first.edge, second.edge });
                        if (plans.Count > before)
                        {
                            chained.Add(first.index);
                            chained.Add(second.index);
                        }
                    }
                }
            }
            foreach (var edge in used.Where(e => !chained.Contains(e.index)))
            {
                if (elevator || edge.covered || edge.edge.Reference == null)
                {
                    var plan = Add(t, elevator ? "昇降路の通り芯からの寸法" : "床端部・入隅の位置寸法", edge.direction, edge.along,
                        edge.station + offset, false, new[] { edge.grid, edge.edge });
                    if (edge.covered && plan != null) plan.CheckSharedBoardEdge = true;
                    continue;
                }

                double c = edge.edge.Coordinate;
                var gridLines = Grids(edge.direction).ToList();
                var before = gridLines.Where(g => g.Coordinate < c - Tolerance)
                    .OrderByDescending(g => g.Coordinate).FirstOrDefault();
                var after = gridLines.Where(g => g.Coordinate > c + Tolerance)
                    .OrderBy(g => g.Coordinate).FirstOrDefault();
                if (before != null && after != null && edge.grid != null && Math.Abs(edge.grid.Coordinate - c) > Tolerance)
                {
                    Add(t, "通り芯間の床端部位置", edge.direction, edge.along,
                        edge.station + offset, false, new[] { before, edge.edge, after });
                    var total = Add(t, "床端部の基準通り芯間寸法", edge.direction, edge.along,
                        edge.station + offset + RowGap, false, new[] { before, after });
                    if (total != null) total.CheckExistingAxisSegment = true;
                }
                else
                    Add(t, "床端部の位置寸法", edge.direction, edge.along, edge.station + offset, false, new[] { edge.grid, edge.edge });

                var travel = points[(edge.index + 1) % n] - points[edge.index];
                double inward = Math.Sign(XYZ.BasisZ.CrossProduct(travel).DotProduct(edge.direction) * handedness);
                var innerGrid = inward > 0 ? after : before;
                if (inward != 0 && innerGrid != null)
                    FloorToBeam(t, edge.direction, edge.along, edge.station, edge.edge, innerGrid, inward);
            }
        }

        private void FloorToBeam(Target floor, XYZ direction, XYZ along, double station, Witness board,
            Witness innerGrid, double inward)
        {
            // A support must cross this section, lie on the slab side, and be within the same grid bay.
            double bayDepth = (innerGrid.Coordinate - board.Coordinate) * inward;
            var floorBounds = BoxPoints(floor.Element.get_BoundingBox(null)).Select(floor.Transform.OfPoint).ToList();
            if (floorBounds.Count == 0) return;
            var floorLevel = UnderSlabBeamFinder.WorldLevel(floor.Element, floor.Transform, floor.Element.LevelId);
            double floorTop = floorBounds.Max(p => p.Z), floorBottom = floorBounds.Min(p => p.Z);
            var candidates = beams.Where(b => Math.Abs(b.tangent.DotProduct(along)) > Parallel)
                .Select(b =>
                {
                    var points = BoxPoints(b.target.Element.get_BoundingBox(null)).Select(b.target.Transform.OfPoint).ToList();
                    return (b.target, b.geometry, Points: points);
                })
                .Where(b => b.Points.Count > 0 && b.Points.Min(p => p.DotProduct(along)) - Tolerance <= station &&
                    b.Points.Max(p => p.DotProduct(along)) + Tolerance >= station &&
                    UnderSlabBeamFinder.FitsBelowSlab(b.Points.Max(p => p.Z), floorBottom, floorTop,
                        UnderSlabBeamFinder.BeamLevel((FamilyInstance)b.target.Element, b.target.Transform), floorLevel, Mm(30)))
                .Select(b => (b.target, Section: BeamSection(b.target, (FamilyInstance)b.target.Element, b.geometry, direction, along, station)))
                .Where(b => b.Section.Low != null)
                .Select(b => (b.target, b.Section, Near: inward > 0 ? b.Section.Low : b.Section.High,
                    Far: inward > 0 ? b.Section.High : b.Section.Low))
                .Select(b => (b.target, b.Section, b.Near, b.Far, Gap: (b.Near.Coordinate - board.Coordinate) * inward))
                .Where(b => b.Gap >= -Tolerance && b.Gap <= bayDepth + Tolerance)
                .OrderBy(b => b.Gap).ThenBy(b => b.target.Link.Id.Value).ThenBy(b => b.target.Element.Id.Value).ToList();
            var closest = candidates.FirstOrDefault();
            if (closest.target == null || closest.Gap <= Tolerance) return;

            beamWidthPlans.TryGetValue(closest.target, out var separateWidth);
            var marks = new List<Witness> { board, closest.Near };
            bool canIncludeWidth = beamWidth != BeamWidthMode.Omit && separateWidth?.ReplacedBy == null;
            if (canIncludeWidth && beamWidth == BeamWidthMode.Split)
            {
                var center = WidthCenter(innerGrid, closest.Section.BodyLow, closest.Section.BodyHigh, closest.Section.Center);
                if (center != null) marks.Add(center);
                else
                {
                    Note(closest.target, "床端部と梁幅を分割するための梁芯参照が見つかりません。");
                    canIncludeWidth = false;
                }
            }
            if (canIncludeWidth) marks.Add(closest.Far);
            var local = Add(floor, "床端部から梁までの寸法", direction, along,
                station + offset - RowGap, false, marks);
            if (local == null || !canIncludeWidth) return;

            var position = Add(floor, "床端部から梁側面までの寸法", direction, along,
                station + offset - RowGap, false, new[] { board, closest.Near });
            if (position != null) position.ReplacedBy = local;
            if (separateWidth != null && separateWidth.Marks.All(w => local.Marks.Any(included => Same(w, included))))
                separateWidth.ReplacedBy = local;
        }

        private bool CoincidentSpan(Target t, XYZ direction, XYZ along, double coordinate, double a, double b)
        {
            double lo = Math.Min(a, b), hi = Math.Max(a, b);
            return GetGeometry(t).Faces.Any(f => Math.Abs(f.Normal.DotProduct(direction)) > Parallel &&
                Math.Abs(f.Points[0].DotProduct(direction) - coordinate) <= Tolerance &&
                f.Points.Min(p => p.DotProduct(along)) <= lo + Tolerance &&
                f.Points.Max(p => p.DotProduct(along)) >= hi - Tolerance);
        }

        private void Elevator(Target t)
        {
            var g = GetGeometry(t);
            var edges = g.Edges.Where(e => e.Edge != null && Math.Abs(e.A.Z - e.B.Z) <= Tolerance).ToList();
            if (edges.Count == 0)
            { Note(t, "昇降路に寸法記入できる床端部が見つかりません。モデルを確認してください。"); return; }
            var floorEdges = geometry.Where(pair => pair.Key.Element is Floor).ToList();
            if (floorEdges.Count == 0) { Note(t, "表示されている床の範囲を特定できません。昇降路の位置を確認してください。"); return; }
            foreach (var (dir, along) in new[] { (x, y), (y, x) })
            {
                var aligned = edges.Where(e => Math.Abs(Flat(e.B - e.A).Normalize().DotProduct(along)) > Parallel).ToList();
                if (aligned.Count == 0) continue;
                foreach (var edge in new[] { aligned.OrderBy(e => e.A.DotProduct(dir)).First(), aligned.OrderBy(e => e.A.DotProduct(dir)).Last() }.Distinct())
                {
                    double c = edge.A.DotProduct(dir), mid = ((edge.A + edge.B) / 2).DotProduct(along);
                    var candidate = floorEdges.SelectMany(pair => SideFaces(pair.Value, dir, along, mid))
                        .FirstOrDefault(face => Math.Abs(face.Points[0].DotProduct(dir) - c) < Tolerance);
                    if (candidate == null) { Note(t, "昇降路の端部に一致する、寸法記入可能な床端部が見つかりません。"); continue; }
                    var witness = FaceWitness(candidate, dir);
                    Add(t, "昇降路の通り芯からの寸法", dir, along, mid + offset, false, new[] { NearestGrid(dir, c), witness });
                    ElevatorBeam(t, dir, along, mid, witness);
                }
            }
        }

        private void ElevatorBeam(Target t, XYZ direction, XYZ along, double station, Witness edge)
        {
            var candidates = beams.Where(b => b.target.Element is FamilyInstance && Math.Abs(b.tangent.DotProduct(along)) > Parallel)
                .Select(b => BeamSection(b.target, (FamilyInstance)b.target.Element, b.geometry, direction, along, station))
                .Where(b => b.Low != null)
                .SelectMany(b => new[] { (side: b.Low, b.Center), (side: b.High, b.Center) })
                .OrderBy(b => Math.Abs(b.side.Coordinate - edge.Coordinate)).ToList();
            if (candidates.Count == 0) { Note(t, "昇降路の端部に平行で、基準に使える梁が見つかりません。"); return; }
            var beam = candidates.First();
            Add(t, "昇降路の梁側面からの寸法", direction, along, station + 2 * offset, false, new[] { beam.side, edge });
            Add(t, "昇降路の梁芯からの寸法", direction, along, station + 3 * offset, false,
                new[] { beam.Center, edge });
        }
    }
}
