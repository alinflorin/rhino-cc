using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoCC.Core.Geometry
{
    /// <summary>Convex hull and area of 2D points. RhinoCommon 7 has no CreateConvexHull2d, so we carry our own.</summary>
    public static class Hull2D
    {
        /// <summary>Hull in counter-clockwise order (monotone chain); fewer than 3 points come back as they are.</summary>
        public static List<(double X, double Y)> Hull(IEnumerable<(double X, double Y)> points)
        {
            var p = points.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
            if (p.Count < 3) return p;
            var h = new List<(double X, double Y)>();
            for (var pass = 0; pass < 2; pass++)
            {
                var start = h.Count;
                foreach (var v in pass == 0 ? p : Enumerable.Reverse(p))
                {
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], v) <= 0) h.RemoveAt(h.Count - 1);
                    h.Add(v);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        public static double Area(IReadOnlyList<(double X, double Y)> hull)
        {
            double a = 0;
            for (var i = 0; i < hull.Count; i++)
            {
                var u = hull[i];
                var v = hull[(i + 1) % hull.Count];
                a += u.X * v.Y - v.X * u.Y;
            }
            return Math.Abs(a) / 2;
        }

        /// <summary>Longest distance between two of the points: the length of a line contact.</summary>
        public static double Extent(IReadOnlyList<(double X, double Y)> points)
        {
            double best = 0;
            for (var i = 0; i < points.Count; i++)
                for (var j = i + 1; j < points.Count; j++)
                    best = Math.Max(best, Math.Sqrt(Math.Pow(points[i].X - points[j].X, 2) + Math.Pow(points[i].Y - points[j].Y, 2)));
            return best;
        }

        private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
