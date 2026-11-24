using System.Collections.Generic;
using System.Linq;

namespace RevitAutoAnnotate
{
    internal static class BeamChainSections
    {
        internal static IEnumerable<(double Low, double High, List<int> Indices)> Find(
            IList<(double Low, double High)> spans, double clearance)
        {
            var remaining = Enumerable.Range(0, spans.Count)
                .Where(i => spans[i].High - spans[i].Low > clearance)
                .OrderBy(i => spans[i].High).ThenBy(i => spans[i].Low).ToList();
            while (remaining.Count > 0)
            {
                double earliestEnd = spans[remaining[0]].High;
                var group = remaining.Where(i => spans[i].Low < earliestEnd - clearance).ToList();
                if (group.Count > 1)
                    yield return (group.Max(i => spans[i].Low), earliestEnd, group);
                var used = new HashSet<int>(group);
                remaining.RemoveAll(used.Contains);
            }
        }
    }
}
