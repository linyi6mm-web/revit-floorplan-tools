using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Autodesk.Revit.DB;

namespace RevitAutoAnnotate
{
    internal sealed class FamilyMap
    {
        private readonly Dictionary<string, string[]> names;
        private static readonly Dictionary<string, string> standardColumnParameters = new Dictionary<string, string>
        {
            { "BodyWidth", "width_X" }, { "BodyDepth", "width_Y" },
            { "SideLow", "added_thickness_X_negative" }, { "SideHigh", "added_thickness_X_positive" },
            { "DepthLow", "added_thickness_Y_negative" }, { "DepthHigh", "added_thickness_Y_positive" }
        };

        public FamilyMap()
        {
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "FamilyMap.xml");
            names = XDocument.Load(path).Root.Elements("Names").ToDictionary(
                n => (string)n.Attribute("role"), n => n.Value.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray());
        }

        public bool Matches(string role, string text) =>
            names.TryGetValue(role, out var list) && list.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

        public double? Length(Element e, string role)
        {
            names.TryGetValue(role, out var list);
            IEnumerable<string> parameters = list ?? Array.Empty<string>();
            var category = (BuiltInCategory)(e.Category?.Id.Value ?? 0);
            if ((category == BuiltInCategory.OST_StructuralColumns || category == BuiltInCategory.OST_Columns) &&
                standardColumnParameters.TryGetValue(role, out var standardName))
                parameters = parameters.Append(standardName);
            var type = e.Document.GetElement(e.GetTypeId());
            foreach (string name in parameters)
            foreach (Element owner in new[] { e, type })
            {
                if (owner == null) continue;
                bool columnSectionWidth = name == "width_X" || name == "width_Y";
                var matches = owner.GetParameters(name).Where(p => p.HasValue && p.StorageType == StorageType.Double &&
                    (p.Definition.GetDataType() == SpecTypeId.Length ||
                     (columnSectionWidth && p.Definition.GetDataType() == SpecTypeId.SectionProperty))).ToList();
                if (matches.Count == 1) return matches[0].AsDouble();
            }
            return null;
        }
    }

    internal sealed class Target
    {
        public readonly RevitLinkInstance Link;
        public readonly Element Element;
        public readonly Transform Transform;

        public Target(RevitLinkInstance link, Element element)
        {
            Link = link;
            Element = element;
            Transform = link?.GetTotalTransform() ?? Transform.Identity;
        }

        public string Name => (Link == null ? "" : Link.Name + " / ") + Element.Name + " #" + Element.Id.Value;
        public long LinkId => Link?.Id.Value ?? ElementId.InvalidElementId.Value;
        public Reference InHost(Reference reference) => Link == null ? reference : reference?.CreateLinkReference(Link);
        public Reference SelectionReference => InHost(new Reference(Element));

        public static RevitLinkGraphicsSettings ReadLinkOverrides(View view, RevitLinkInstance link)
        {
            try { return view.GetLinkOverrides(link.Id) ?? view.GetLinkOverrides(link.GetTypeId()); }
            // Revit 2024 rejects this optional query in some views, including ones that allow other graphics overrides.
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
        }

        public static string TypeName(Element element)
        {
            var symbol = (element as FamilyInstance)?.Symbol;
            return element.Name + " " + symbol?.FamilyName + " " + element.Document.GetElement(element.GetTypeId())?.Name;
        }
    }

    internal sealed class Witness
    {
        public readonly Reference Reference;
        public readonly double Coordinate;
        public readonly bool Grid;
        public Witness(Reference reference, double coordinate, bool grid = false)
        {
            Reference = reference; Coordinate = coordinate; Grid = grid;
        }
    }

    internal sealed class EdgeTrace
    {
        public XYZ A, B;
        public Reference Edge, AtA, AtB;
    }

    internal sealed class FaceTrace
    {
        public XYZ Normal;
        public IList<XYZ> Points;
        public Reference Reference;
        public bool Steel;
        public bool Added;
    }

    internal sealed class ModelGeometry
    {
        public const double Parallel = 0.99999999;
        public static double Mm(double x) => x / 304.8;
        public static readonly double Tolerance = Mm(1);
        public readonly List<FaceTrace> Faces = new List<FaceTrace>();
        public readonly List<EdgeTrace> Edges = new List<EdgeTrace>();
        public readonly List<EdgeTrace> ColumnPlanLines = new List<EdgeTrace>();
        public bool HasNonPlanarSide { get; private set; }
        private readonly bool classifyColumnSteel;

        public ModelGeometry(Target target, bool includeNonVisible = false, View geometryView = null)
        {
            var category = (BuiltInCategory)(target.Element.Category?.Id.Value ?? 0);
            classifyColumnSteel = category == BuiltInCategory.OST_StructuralColumns || category == BuiltInCategory.OST_Columns;
            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = includeNonVisible };
            if (geometryView != null) options.View = geometryView;
            else options.DetailLevel = ViewDetailLevel.Fine;
            var geometry = target.Element.get_Geometry(options);
            if (geometry != null) Read(target, geometry, target.Transform, false);
        }

        private void Read(Target target, GeometryElement geometry, Transform transform, bool steelPart)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is GeometryInstance inst)
                {
                    bool nestedSteel = steelPart;
                    if (classifyColumnSteel && !nestedSteel &&
                        target.Element.Document.GetElement(inst.GetSymbolGeometryId().SymbolId) is FamilySymbol symbol &&
                        symbol.Id != target.Element.GetTypeId())
                        nestedSteel = ColumnSteelName(symbol.FamilyName + " " + symbol.Name);
                    // Only the original symbol geometry provides dimensionable references.
                    Read(target, inst.GetSymbolGeometry(), transform.Multiply(inst.Transform), nestedSteel);
                    continue;
                }
                if (classifyColumnSteel && !steelPart && obj is Line planLine && planLine.IsBound)
                {
                    var category = (target.Element.Document.GetElement(planLine.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory;
                    if (category == null || !ColumnSteelName(category.Name))
                        ColumnPlanLines.Add(new EdgeTrace { A = transform.OfPoint(planLine.GetEndPoint(0)),
                            B = transform.OfPoint(planLine.GetEndPoint(1)), Edge = target.InHost(planLine.Reference) });
                    continue;
                }
                if (!(obj is Solid solid) || solid.Faces.Size == 0 || solid.Volume < 1e-10) continue;
                foreach (Face raw in solid.Faces)
                {
                    if (!(raw is PlanarFace face))
                    {
                        if (raw is CylindricalFace arc && Math.Abs(transform.OfVector(arc.Axis).Z) > Parallel)
                            HasNonPlanarSide = true;
                        continue;
                    }
                    var points = face.EdgeLoops.Cast<EdgeArray>().SelectMany(a => a.Cast<Edge>())
                        .SelectMany(e => e.Tessellate()).Select(transform.OfPoint).ToList();
                    if (points.Count == 0) continue;
                    var material = target.Element.Document.GetElement(face.MaterialElementId) as Material;
                    string materialText = material == null ? "" : material.Name + " " + material.MaterialClass;
                    string subcategory = (target.Element.Document.GetElement(face.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                    Faces.Add(new FaceTrace
                    {
                        Normal = transform.OfVector(face.FaceNormal).Normalize(),
                        Points = points,
                        Reference = target.InHost(face.Reference),
                        Steel = classifyColumnSteel ? !ConcreteName(materialText) &&
                            (steelPart || ColumnSteelName(materialText) || ColumnSteelName(subcategory)) :
                            Contains(materialText, "steel", "鋼材", "鉄骨", "钢材", "S235", "S355"),
                        Added = Contains(materialText + " " + subcategory, "ふかし", "フカシ", "増打", "増し打ち", "加厚", "fukashi")
                    });
                }
                foreach (Edge edge in solid.Edges)
                {
                    if (!(edge.AsCurve() is Line line)) continue;
                    var item = new EdgeTrace
                    {
                        A = transform.OfPoint(line.GetEndPoint(0)),
                        B = transform.OfPoint(line.GetEndPoint(1)),
                        Edge = target.InHost(edge.Reference)
                    };
                    if (item.Edge != null)
                    {
                        item.AtA = target.InHost(edge.GetEndPointReference(0));
                        item.AtB = target.InHost(edge.GetEndPointReference(1));
                    }
                    Edges.Add(item);
                }
            }
        }

        internal static bool ConcreteName(string label) =>
            Contains(label, "SRC", "ＳＲＣ", "鉄骨鉄筋", "コンクリート", "concrete", "混凝土");

        internal static bool NamedFukashi(FamilyInstance instance) =>
            Contains(instance.Symbol?.FamilyName + " " + instance.Name, "フカシ", "ふかし", "増打", "増し打ち", "fukashi");

        internal static bool IsColumnFukashi(FamilyInstance instance)
        {
            var category = (BuiltInCategory)(instance.SuperComponent?.Category?.Id.Value ?? 0);
            return (category == BuiltInCategory.OST_StructuralColumns || category == BuiltInCategory.OST_Columns) && NamedFukashi(instance);
        }

        internal static bool ColumnSteelName(string label) => !ConcreteName(label) &&
            Contains(label, "steel", "鋼材", "鉄骨", "钢材", "钢柱", "S235", "S355", "SN400", "SN490", "BCR", "BCP");

        public static bool Contains(string text, params string[] words) =>
            words.Any(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

        public static Witness FaceWitness(FaceTrace face, XYZ direction) =>
            new Witness(face.Reference, face.Points[0].DotProduct(direction));

        public static double Center(FaceTrace face, XYZ direction) => face.Points.Average(p => p.DotProduct(direction));

        public Witness AtPoint(XYZ point, XYZ direction)
        {
            // A straight vertical edge represents the plan corner, independently of the wall's orientation.
            foreach (var e in Edges)
                if (Math.Abs(e.A.Z - e.B.Z) > Tolerance && PlanDistance(e.A, e.B) < Tolerance &&
                    PlanDistance(e.A, point) < Tolerance && e.Edge != null)
                    return new Witness(e.Edge, point.DotProduct(direction));
            foreach (var e in Edges)
            {
                if (e.AtA != null && PlanDistance(e.A, point) < Tolerance)
                    return new Witness(e.AtA, point.DotProduct(direction));
                if (e.AtB != null && PlanDistance(e.B, point) < Tolerance)
                    return new Witness(e.AtB, point.DotProduct(direction));
            }
            return null;
        }

        public static double PlanDistance(XYZ a, XYZ b) => Flat(a - b).GetLength();
        public static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        public static IList<XYZ> BoxPoints(BoundingBoxXYZ box)
        {
            var result = new List<XYZ>();
            if (box == null) return result;
            for (int i = 0; i < 8; i++)
                result.Add(box.Transform.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X,
                    (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z)));
            return result;
        }
    }
}
