using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using static RevitAutoAnnotate.ModelGeometry;
using Owner = System.ValueTuple<long, long>;

namespace RevitAutoAnnotate
{
    internal sealed class ColumnDimensionCoverage
    {
        private readonly ILookup<(Owner, Owner), (XYZ Direction, XYZ Midpoint, double Length)> existing;

        public ColumnDimensionCoverage(View view, IEnumerable<Dimension> dimensions)
        {
            existing = dimensions.Where(d => d.Category?.Id.Value == (long)BuiltInCategory.OST_Dimensions &&
                !d.IsHidden(view)).SelectMany(Segments).ToLookup(s => s.Owners, s => s.Geometry);
        }

        private static (Owner, Owner) Owners(Reference first, Reference second)
        {
            var a = new Owner(first.ElementId.Value, first.LinkedElementId.Value);
            var b = new Owner(second.ElementId.Value, second.LinkedElementId.Value);
            return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        }

        private static IEnumerable<((Owner, Owner) Owners, (XYZ Direction, XYZ Midpoint, double Length) Geometry)>
            Segments(Dimension dimension)
        {
            if (!(dimension.Curve is Line line)) yield break;
            var refs = dimension.References.Cast<Reference>().ToArray();
            var segments = dimension.Segments;
            int count = segments?.Size ?? 0;
            if (refs.Length == 2 && count == 0 && dimension.Value is double length)
                yield return (Owners(refs[0], refs[1]), (line.Direction, dimension.Origin, length));
            if (count == 0 || count != refs.Length - 1) yield break;
            for (int i = 0; i < count; i++)
            {
                var segment = segments.get_Item(i);
                if (segment.Value is double value)
                    yield return (Owners(refs[i], refs[i + 1]), (line.Direction, segment.Origin, value));
            }
        }

        private bool Contains(XYZ direction, Witness low, Witness high)
        {
            double length = high.Coordinate - low.Coordinate;
            foreach (var segment in existing[Owners(low.Reference, high.Reference)])
            {
                if (Math.Abs(segment.Direction.DotProduct(direction)) < Parallel) continue;
                double midpointError = Math.Abs(segment.Midpoint.DotProduct(direction) - (low.Coordinate + high.Coordinate) / 2);
                // Compare both witness positions, independent of dimension-line or text placement.
                if (midpointError + Math.Abs(segment.Length - length) / 2 <= Tolerance) return true;
            }
            return false;
        }

        public IEnumerable<(DimensionPlan Plan, bool Exists)> Partition(DimensionPlan plan)
        {
            var marks = plan.Marks;
            int start = 0;
            bool exists = Contains(plan.Direction, marks[0], marks[1]);
            for (int i = 1; i < marks.Count - 1; i++)
            {
                bool next = Contains(plan.Direction, marks[i], marks[i + 1]);
                if (next == exists) continue;
                yield return (plan.WithMarks(start, i - start + 1), exists);
                start = i;
                exists = next;
            }
            yield return (plan.WithMarks(start, marks.Count - start), exists);
        }
    }
}
