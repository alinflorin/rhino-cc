using System.Collections.Generic;
using System.Linq;

namespace RhinoCC.Core.Geometry
{
    /// <summary>Connected groups of nodes 0..count-1 joined by edges (union-find); the biggest group first.</summary>
    public static class Components
    {
        public static List<List<int>> Of(int count, IEnumerable<(int A, int B)> edges)
        {
            var parent = Enumerable.Range(0, count).ToArray();
            int Find(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }
            foreach (var e in edges) parent[Find(e.A)] = Find(e.B);
            return Enumerable.Range(0, count).GroupBy(Find).Select(g => g.ToList()).OrderByDescending(g => g.Count).ThenBy(g => g[0]).ToList();
        }
    }
}
