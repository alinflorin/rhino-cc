using System;
using System.Collections.Generic;
using System.Linq;
using RhinoCC.Core.Geometry;

namespace RhinoCC.Core.Printing
{
    /// <summary>Where the casting sprue joins the piece.</summary>
    public sealed class SprueSite
    {
        public Vec3 At, Normal;
        /// <summary>Metal thickness behind the join, mm.</summary>
        public double Thickness;
        public bool Manual;
    }

    public sealed class SprueReach
    {
        public int Regions;
        public double Farthest;
        public List<Vec3> Where = new List<Vec3>();
    }

    /// <summary>
    /// Placement and checks of the wax-channel stub, in millimetres. Ported from frederic-coultier's meshtools.rs / insight.rs.
    /// The stub is a tube along the surface normal, embedded a little into the piece and flaring at its foot.
    /// </summary>
    public static class Sprue
    {
        /// <summary>Thin sections farther than this (mm) from the sprue foot may freeze before the metal reaches them.</summary>
        public const double Reach = 14;
        /// <summary>The sprue should be about as thick as the section it feeds.</summary>
        public const double RatioLow = 0.6, RatioHigh = 1.5;
        /// <summary>Walls thinner than this are sections the metal can fail to fill.</summary>
        public const double FillThin = 0.7;

        /// <summary>
        /// The thickest, least visible spot: candidates are ranked by local thickness, favouring faces that look towards the
        /// piece's centre; the first one with free air in front of it wins. With <paramref name="at"/> the spot is that point,
        /// snapped to the closest point of the surface.
        /// </summary>
        public static SprueSite Plan(TriMesh m, double diameter, double length, Vec3? at)
        {
            if (m.TriCount == 0) return null;
            var maxD = Math.Max(1.5 * diameter, 2.5);

            if (at.HasValue)
            {
                double bestD = double.MaxValue; Vec3 bq = default, bn = default; var found = false;
                for (var t = 0; t < m.TriCount; t++)
                {
                    m.Tri(t, out var a, out var b, out var c);
                    var cr = (b - a).Cross(c - a);
                    var l = cr.Length;
                    if (l < 1e-9) continue;
                    var q = TriMesh.ClosestOnTri(at.Value, a, b, c);
                    var d = q.DistanceTo(at.Value);
                    if (d < bestD) { bestD = d; bq = q; bn = cr * (1 / l); found = true; }
                }
                if (!found) return null;
                var thick = m.RayHit(bq, -bn, -1, maxD) ?? maxD;
                return new SprueSite { At = bq, Normal = bn, Thickness = thick, Manual = true };
            }

            var info = new (Vec3 cen, Vec3 n, bool ok)[m.TriCount];
            var centre = new Vec3(); double asum = 0;
            for (var t = 0; t < m.TriCount; t++)
            {
                m.Tri(t, out var a, out var b, out var c);
                var cr = (b - a).Cross(c - a);
                var l = cr.Length;
                if (l < 1e-9) continue;
                var cen = (a + b + c) * (1.0 / 3);
                centre = centre + cen * l;
                asum += l;
                info[t] = (cen, cr * (1 / l), true);
            }
            if (asum > 0) centre = centre * (1 / asum);

            var stride = Math.Max(m.TriCount / 700, 1);
            var cands = new List<KeyValuePair<double, int>>();
            for (var i = 0; i < m.TriCount; i += stride)
            {
                if (!info[i].ok) continue;
                var thick = m.RayHit(info[i].cen, -info[i].n, i, maxD) ?? maxD;
                var d = info[i].cen - centre;
                var dd = d.Length;
                var o = dd > 1e-9 ? d.Dot(info[i].n) / dd : 0;
                cands.Add(new KeyValuePair<double, int>(thick * (1 + Math.Max(-o, 0)), i));
            }
            foreach (var cand in cands.OrderByDescending(c => c.Key).Take(25))
            {
                var inf = info[cand.Value];
                if (m.RayHit(inf.cen + inf.n * 0.05, inf.n, cand.Value, length + 0.5).HasValue) continue; // the stub would run into the piece
                var thick = m.RayHit(inf.cen, -inf.n, cand.Value, maxD) ?? maxD;
                return new SprueSite { At = inf.cen, Normal = inf.n, Thickness = thick };
            }
            return null;
        }

        /// <summary>Joined thickness against sprue diameter: true when the ratio is in the range that feeds well.</summary>
        public static bool GoodRatio(double diameter, double thickness, out double ratio)
        {
            ratio = diameter / Math.Max(thickness, 1e-6);
            return ratio >= RatioLow && ratio <= RatioHigh;
        }

        /// <summary>Thin sections (wall under <see cref="FillThin"/>) farther than <see cref="Reach"/> from the sprue foot, as places apart.</summary>
        public static SprueReach CheckReach(TriMesh m, Vec3 at)
        {
            var far = new List<KeyValuePair<double, Vec3>>();
            var stride = Math.Max(m.TriCount / 1500, 1);
            for (var t = 0; t < m.TriCount; t += stride)
            {
                m.Tri(t, out var a, out var b, out var c);
                var cr = (b - a).Cross(c - a);
                var l = cr.Length;
                if (l < 1e-9) continue;
                var n = cr * (1 / l);
                var cen = (a + b + c) * (1.0 / 3);
                var wall = m.RayHit(cen, -n, t, FillThin);
                if (!wall.HasValue) continue;
                var d = cen.DistanceTo(at);
                if (d > Reach) far.Add(new KeyValuePair<double, Vec3>(d, cen));
            }
            var seeds = new List<KeyValuePair<double, Vec3>>();
            foreach (var f in far.OrderByDescending(x => x.Key))
                if (!seeds.Any(s => s.Value.DistanceTo(f.Value) < 2)) seeds.Add(f);
            return new SprueReach { Regions = seeds.Count, Farthest = seeds.Count > 0 ? seeds[0].Key : 0, Where = seeds.Take(8).Select(s => s.Value).ToList() };
        }

        /// <summary>The stub as rings along its axis: (distance along the normal from the surface point, radius).</summary>
        public static (double h, double r)[] Rings(double diameter, double length)
        {
            var r = diameter / 2;
            return new[] { (-0.4, r * 0.9), (0.0, r * 0.9), (1.6, r), (Math.Max(length, 1.7), r) };
        }
    }
}
