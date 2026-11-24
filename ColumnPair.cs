using System.Collections.Generic;
using System.Linq;

namespace RevitAutoAnnotate
{
    internal sealed class ColumnPair
    {
        public readonly Target Concrete, Steel;
        public readonly (long Link, string Element) ConcreteId, SteelId;
        public readonly double Elevation, CutDistance, CenterOffset;
        public readonly bool IsSubcomponent, AtCut;

        public ColumnPair(Target concrete, Target steel, double elevation, bool atCut, double cutDistance,
            double centerOffset, bool isSubcomponent)
        {
            Concrete = concrete;
            Steel = steel;
            ConcreteId = (concrete.LinkId, concrete.Element.UniqueId);
            SteelId = (steel.LinkId, steel.Element.UniqueId);
            Elevation = elevation;
            AtCut = atCut;
            CutDistance = cutDistance;
            CenterOffset = centerOffset;
            IsSubcomponent = isSubcomponent;
        }

        public static List<ColumnPair> Preferred(IEnumerable<ColumnPair> candidates, double tolerance)
        {
            var remaining = candidates.ToList();
            if (remaining.Count < 2) return remaining;
            if (remaining.Any(pair => pair.IsSubcomponent))
                remaining = remaining.Where(pair => pair.IsSubcomponent).ToList();
            if (remaining.Any(pair => pair.AtCut))
                remaining = remaining.Where(pair => pair.AtCut).ToList();
            double closestCut = remaining.Min(pair => pair.CutDistance);
            remaining = remaining.Where(pair => pair.CutDistance - closestCut <= tolerance).ToList();
            double closestCenter = remaining.Min(pair => pair.CenterOffset);
            return remaining.Where(pair => pair.CenterOffset - closestCenter <= tolerance).ToList();
        }

        public static void AddTagOwners(IEnumerable<ColumnPair> pairs, Dictionary<Target, Target> owners, double tolerance)
        {
            foreach (var group in pairs.GroupBy(pair => pair.Steel))
            {
                var preferred = Preferred(group, tolerance);
                if (preferred.Count == 1) owners.Add(group.Key, preferred[0].Concrete);
            }
        }
    }
}
