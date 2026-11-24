using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using static RevitAutoAnnotate.ModelGeometry;

namespace RevitAutoAnnotate
{
    internal static class UnderSlabBeamFinder
    {
        private sealed class Slab
        {
            public Transform ToLocal;
            public IList<PlanarFace> TopFaces;
            public IList<XYZ> Bounds;
            public double Bottom, Top;
            public double? Level;
        }

        public static List<Target> Find(Document host, View view, List<RevitLinkInstance> links,
            List<Target> linkedFloors, HashSet<(long instance, long element)> known, List<ReviewItem> notes)
        {
            var result = new List<Target>();
            var story = view.GenLevel;
            if (story == null || links.Count == 0) return result;

            double baseElevation = story.ProjectElevation;
            var otherStories = new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => l.Id != story.Id && l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 1)
                .Select(l => l.ProjectElevation)
                .Where(z => Math.Abs(z - baseElevation) > Tolerance).ToList();
            double? lowerStory = otherStories.Where(z => z < baseElevation).Select(z => (double?)z).Max();
            var floors = linkedFloors.Select(t => ((Floor)t.Element, t.Transform)).Concat(
                new FilteredElementCollector(host, view.Id).OfClass(typeof(Floor)).Cast<Floor>()
                    .Select(f => (f, Transform.Identity)));

            var slabs = new List<Slab>();
            foreach (var (floor, transform) in floors)
            {
                try
                {
                    var points = BoxPoints(floor.get_BoundingBox(null)).Select(transform.OfPoint).ToList();
                    if (points.Count == 0) continue;
                    var level = WorldLevel(floor, transform, floor.LevelId);
                    double elevation = level ?? points.Max(p => p.Z);
                    if (otherStories.Any(z => Math.Abs(z - elevation) + Tolerance < Math.Abs(baseElevation - elevation)))
                        continue;
                    var faces = HostObjectUtils.GetTopFaces(floor)
                        .Select(r => floor.GetGeometryObjectFromReference(r)).OfType<PlanarFace>()
                        .Where(f => Math.Abs(transform.OfVector(f.FaceNormal).Z) > Parallel).ToList();
                    if (faces.Count == 0) continue;
                    slabs.Add(new Slab { ToLocal = transform.Inverse, TopFaces = faces, Bounds = points,
                        Bottom = points.Min(p => p.Z), Top = points.Max(p => p.Z), Level = level });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    notes.Add(new ReviewItem { Message = "床下の梁の検索に必要な床形状を取得できませんでした：" + ex.Message });
                }
            }
            if (slabs.Count == 0) return result;

            // Search only the current storey below a visible slab, even if the host view range excluded the beam.
            double searchBottom = lowerStory ?? slabs.Min(s => s.Bottom) - Mm(3000);
            double contactTolerance = Mm(30);
            var framing = new ElementId(BuiltInCategory.OST_StructuralFraming);
            foreach (var link in links)
            {
                var doc = link.GetLinkDocument();
                if (doc == null) continue;
                try
                {
                    var settings = Target.ReadLinkOverrides(view, link);
                    View linkedView = settings?.LinkedViewId != null && settings.LinkedViewId != ElementId.InvalidElementId
                        ? doc.GetElement(settings.LinkedViewId) as View : null;
                    if (settings?.LinkVisibilityType == LinkVisibility.ByLinkView && linkedView != null)
                    {
                        if (linkedView.GetCategoryHidden(framing) ||
                            !FilteredElementCollector.IsViewValidForElementIteration(doc, linkedView.Id)) continue;
                    }
                    else if (view.GetCategoryHidden(framing)) continue;

                    var transform = link.GetTotalTransform();
                    var toLink = transform.Inverse;
                    var windowPoints = slabs.SelectMany(s => s.Bounds).SelectMany(p => new[]
                    {
                        toLink.OfPoint(new XYZ(p.X, p.Y, Math.Min(searchBottom, p.Z - contactTolerance))),
                        toLink.OfPoint(new XYZ(p.X, p.Y, p.Z + contactTolerance))
                    }).ToList();
                    var boundary = new Outline(
                        new XYZ(windowPoints.Min(p => p.X), windowPoints.Min(p => p.Y), windowPoints.Min(p => p.Z)),
                        new XYZ(windowPoints.Max(p => p.X), windowPoints.Max(p => p.Y), windowPoints.Max(p => p.Z)));
                    var beamCollector = settings?.LinkVisibilityType == LinkVisibility.ByLinkView && linkedView != null
                        ? new FilteredElementCollector(doc, linkedView.Id) : new FilteredElementCollector(doc);
                    foreach (var beam in beamCollector.OfCategory(BuiltInCategory.OST_StructuralFraming)
                        .OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(boundary)).Cast<FamilyInstance>())
                    {
                        var key = (link.Id.Value, beam.Id.Value);
                        if (known.Contains(key) || beam.StructuralType == StructuralType.Brace || IsColumnFukashi(beam) ||
                            !(beam.Location is LocationCurve location) || !(location.Curve is Line axis) ||
                            Math.Abs(transform.OfVector(axis.Direction).Z) > 0.00001) continue;
                        var bounds = BoxPoints(beam.get_BoundingBox(null)).Select(transform.OfPoint).ToList();
                        if (bounds.Count == 0) continue;
                        var center = transform.OfPoint(axis.Evaluate(0.5, true));
                        double top = bounds.Max(p => p.Z);
                        if (top < searchBottom - contactTolerance) continue;
                        var level = BeamLevel(beam, transform);
                        if (!slabs.Any(s => FitsBelowSlab(top, s.Bottom, s.Top, level, s.Level, contactTolerance) &&
                            ContainsPoint(s, center))) continue;
                        known.Add(key);
                        result.Add(new Target(link, beam));
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    notes.Add(new ReviewItem { Message = link.Name + " · 床下の梁を検索できませんでした：" + ex.Message });
                }
            }
            return result;
        }

        internal static bool FitsBelowSlab(double beamTop, double slabBottom, double slabTop,
            double? beamLevel, double? slabLevel, double tolerance) =>
            beamTop <= slabTop + tolerance &&
            (beamTop >= slabBottom - tolerance ||
             (beamLevel.HasValue && slabLevel.HasValue && Math.Abs(beamLevel.Value - slabLevel.Value) <= tolerance));

        internal static double? BeamLevel(FamilyInstance beam, Transform transform)
        {
            var reference = beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            return WorldLevel(beam, transform, reference?.StorageType == StorageType.ElementId
                ? reference.AsElementId() : beam.LevelId);
        }

        internal static double? WorldLevel(Element element, Transform transform, ElementId id) =>
            element.Document.GetElement(id) is Level level
                ? transform.OfPoint(new XYZ(0, 0, level.ProjectElevation)).Z : (double?)null;

        private static bool ContainsPoint(Slab slab, XYZ center)
        {
            var localPoint = slab.ToLocal.OfPoint(center);
            return slab.TopFaces.Any(face =>
            {
                var atTop = new XYZ(localPoint.X, localPoint.Y, face.Origin.Z);
                var hit = face.Project(atTop);
                return hit != null && face.IsInside(hit.UVPoint) && PlanDistance(hit.XYZPoint, atTop) <= Tolerance;
            });
        }
    }
}
