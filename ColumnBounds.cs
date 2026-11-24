using System;

namespace RevitAutoAnnotate
{
    internal readonly struct ColumnBounds
    {
        private readonly double xMin, xMax, yMin, yMax, zMin, zMax;

        public ColumnBounds(double xMin, double xMax, double yMin, double yMax, double zMin, double zMax)
        {
            this.xMin = xMin; this.xMax = xMax;
            this.yMin = yMin; this.yMax = yMax;
            this.zMin = zMin; this.zMax = zMax;
        }

        public (double Elevation, bool AtCut, double CutDistance)? CommonSection(ColumnBounds steel, double? preferred, double tolerance)
        {
            double bottom = Math.Max(zMin, steel.zMin), top = Math.Min(zMax, steel.zMax);
            if (top - bottom <= tolerance || Math.Min(xMax, steel.xMax) - Math.Max(xMin, steel.xMin) <= tolerance ||
                Math.Min(yMax, steel.yMax) - Math.Max(yMin, steel.yMin) <= tolerance) return null;
            if (!preferred.HasValue) return ((bottom + top) / 2, false, 0);
            double cut = preferred.Value;
            if (cut > bottom && cut < top) return (cut, true, 0);
            return ((bottom + top) / 2, false, Math.Max(bottom - cut, cut - top));
        }

        public static bool ContainsSpan(double low, double high, double steelLow, double steelHigh, double tolerance) =>
            steelHigh - steelLow > tolerance && steelLow - low > tolerance && high - steelHigh > tolerance;
    }
}
