using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using static RevitAutoAnnotate.ModelGeometry;

namespace RevitAutoAnnotate
{
    internal sealed class ColumnPlanOutline
    {
        public Witness Left, Right, Bottom, Top;

        private static bool Same(double a, double b) => Math.Abs(a - b) <= Tolerance;

        public static ColumnPlanOutline Find(IEnumerable<EdgeTrace> lines, XYZ x, XYZ y,
            double left, double right, double bottom, double top)
        {
            var edges = lines.Where(e => Same(e.A.Z, e.B.Z))
                .Select(e => (AX: e.A.DotProduct(x), BX: e.B.DotProduct(x), AY: e.A.DotProduct(y),
                    BY: e.B.DotProduct(y), Z: e.A.Z, e.Edge)).ToList();
            var horizontal = edges.Where(e => Same(e.AY, e.BY) && !Same(e.AX, e.BX))
                .Select(e => (Y: e.AY, Left: Math.Min(e.AX, e.BX), Right: Math.Max(e.AX, e.BX), e.Z, e.Edge)).ToList();
            var vertical = edges.Where(e => Same(e.AX, e.BX) && !Same(e.AY, e.BY))
                .Select(e => (X: e.AX, Bottom: Math.Min(e.AY, e.BY), Top: Math.Max(e.AY, e.BY), e.Z, e.Edge)).ToList();
            var candidates = new List<ColumnPlanOutline>();
            foreach (var lower in horizontal.Where(e => e.Y <= bottom + Tolerance && e.Left <= left + Tolerance && e.Right >= right - Tolerance))
            foreach (var upper in horizontal.Where(e => e.Y >= top - Tolerance && Same(e.Left, lower.Left) &&
                Same(e.Right, lower.Right) && Same(e.Z, lower.Z) && e.Y - lower.Y > Tolerance))
            {
                if (Same(left, lower.Left) && Same(right, lower.Right) && Same(bottom, lower.Y) && Same(top, upper.Y)) continue;
                var sides = vertical.Where(e => Same(e.Bottom, lower.Y) && Same(e.Top, upper.Y) && Same(e.Z, lower.Z))
                    .OrderByDescending(e => e.Edge != null).ToList();
                if (!sides.Any(e => Same(e.X, lower.Left)) || !sides.Any(e => Same(e.X, lower.Right))) continue;
                var l = sides.First(e => Same(e.X, lower.Left));
                var r = sides.First(e => Same(e.X, lower.Right));
                candidates.Add(new ColumnPlanOutline { Left = new Witness(l.Edge, l.X), Right = new Witness(r.Edge, r.X),
                    Bottom = new Witness(lower.Edge, lower.Y), Top = new Witness(upper.Edge, upper.Y) });
            }
            return candidates.Where(c => candidates.All(other => c.Left.Coordinate <= other.Left.Coordinate + Tolerance &&
                c.Right.Coordinate >= other.Right.Coordinate - Tolerance && c.Bottom.Coordinate <= other.Bottom.Coordinate + Tolerance &&
                c.Top.Coordinate >= other.Top.Coordinate - Tolerance))
                .OrderByDescending(c => new[] { c.Left, c.Right, c.Bottom, c.Top }.Count(w => w.Reference != null)).FirstOrDefault();
        }
    }
}
