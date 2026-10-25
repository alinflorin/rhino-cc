using System;
using System.Collections.Generic;
using System.Linq;
using RhinoCC.Core.Geometry;

namespace RhinoCC.Core.Printing
{
    /// <summary>One support contact: where a pillar touches the piece, in the model's own frame.</summary>
    public sealed class Support
    {
        public int Id { get; set; }
        public Vec3 P { get; set; }
        /// <summary>"auto" or "manual".</summary>
        public string Source { get; set; } = "manual";
        /// <summary>Own tip radius, or null for the default.</summary>
        public double? Tip { get; set; }
    }

    public sealed class PrintParams
    {
        public double Tilt, Lift, Tip, Base, Raft, RaftRadius;

        public static PrintParams From(PrintSettings s) => new PrintParams
        {
            Tilt = s.Tilt, Lift = s.Supports.Lift, Tip = s.Supports.TipRadius, Base = s.Supports.BaseRadius,
            Raft = s.Supports.RaftThickness, RaftRadius = s.Supports.RaftRadius,
        };
    }

    public sealed class AutoParams
    {
        /// <summary>Faces pointing down within this many degrees of horizontal need support.</summary>
        public double Angle = 45;
        /// <summary>Largest distance one support may serve, mm.</summary>
        public double Spacing = 4;
        /// <summary>Surface sampling resolution, mm.</summary>
        public double Step = 1;
        /// <summary>Prefer inner surfaces by up to this many mm of height.</summary>
        public double Bias = 3;
    }

    public sealed class AutoStats
    {
        public int OverhangFaces, Samples, Added, Kept, Blocked;
    }

    public sealed class AutoResult
    {
        public List<Support> Supports = new List<Support>();
        public int NextId;
        public AutoStats Stats;
    }

    public sealed class TiltRow
    {
        public double Tilt, Overhang, Flat;
        public Vec3 Size;
        public bool Fits;
    }

    public sealed class PrintFrame
    {
        /// <summary>Size of the tilted, lifted piece (z includes the lift).</summary>
        public Vec3 Size;
        public bool Fits;
        /// <summary>Height added to a tilted point so the piece's lowest point sits <c>Lift</c> above the plate.</summary>
        public double Zoff;
    }

    /// <summary>A tapered pillar from the plate (or raft) up to a contact, in the model frame.</summary>
    public sealed class Pillar
    {
        public Support Support;
        public Vec3 Bottom, Top;
        public double BottomRadius, TopRadius;
    }

    /// <summary>A flat piece of the raft: an outline on the plate, extruded <see cref="Thickness"/> along the up direction.</summary>
    public sealed class RaftPiece
    {
        public List<Vec3> Outline = new List<Vec3>();
        public double Thickness;
    }

    public sealed class SupportPlan
    {
        public List<Pillar> Pillars = new List<Pillar>();
        public List<RaftPiece> Raft = new List<RaftPiece>();
        /// <summary>Print "up" in the model frame.</summary>
        public Vec3 Up;
    }

    /// <summary>
    /// Resin-print supports on a mesh, in millimetres: print-orientation ranking, automatic placement of contact points,
    /// and the pillars and raft that carry them. Ported from frederic-coultier's supports.rs. The print frame is the model
    /// turned <c>tilt</c> degrees about X and lifted <c>lift</c> mm above the plate; contacts are kept in the model frame
    /// so they stay put when the tilt changes.
    /// </summary>
    public static class Supports
    {
        public static Vec3 Tilt(Vec3 p, double deg)
        {
            var r = deg * Math.PI / 180;
            double s = Math.Sin(r), c = Math.Cos(r);
            return new Vec3(p.X, p.Y * c - p.Z * s, p.Y * s + p.Z * c);
        }

        public static Vec3 Untilt(Vec3 p, double deg) => Tilt(p, -deg);

        /// <summary>Print "up" seen from the model: the model's z when not tilted.</summary>
        public static Vec3 Up(double tiltDeg) => Untilt(new Vec3(0, 0, 1), tiltDeg);

        public static PrintFrame Frame(TriMesh mesh, double tilt, double lift)
        {
            var t = mesh.Map(p => Tilt(p, tilt));
            if (!t.Bounds(out var lo, out var hi)) return null;
            var size = new Vec3(hi.X - lo.X, hi.Y - lo.Y, hi.Z - lo.Z + lift);
            return new PrintFrame { Size = size, Zoff = lift - lo.Z, Fits = size.X <= BuildVolume.X && size.Y <= BuildVolume.Y && size.Z <= BuildVolume.Z };
        }

        // ------------------------------------------------------------ print orientation

        /// <summary>
        /// Ranks tilts about X from <paramref name="min"/> to <paramref name="max"/>: the overhanging area (needs supports)
        /// plus three times the flat area lying on the plate, best first; tilts that do not fit the printer come last.
        /// </summary>
        public static List<TiltRow> RankTilts(TriMesh mesh, double min, double max, double step, double angle, double lift)
        {
            var ct = Math.Cos(angle * Math.PI / 180);
            var tris = new List<(Vec3 n, double area, int a, int b, int c)>();
            for (var t = 0; t < mesh.TriCount; t++)
            {
                mesh.Tri(t, out var a, out var b, out var c);
                var cr = (b - a).Cross(c - a);
                var l = cr.Length;
                if (l < 1e-12) continue;
                tris.Add((cr * (1 / l), l / 2, mesh.Tris[t * 3], mesh.Tris[t * 3 + 1], mesh.Tris[t * 3 + 2]));
            }
            var rows = new List<TiltRow>();
            for (var tilt = min; tilt <= max + 1e-9; tilt += step)
            {
                var r = tilt * Math.PI / 180;
                double s = Math.Sin(r), c = Math.Cos(r);
                var v = mesh.Map(p => Tilt(p, tilt));
                if (!v.Bounds(out var lo, out var hi)) continue;
                double over = 0, flat = 0;
                foreach (var f in tris)
                {
                    var nz = f.n.Y * s + f.n.Z * c;
                    if (nz < -ct) over += f.area;
                    if (nz < -0.995)
                    {
                        var zc = (v.Verts[f.a].Z + v.Verts[f.b].Z + v.Verts[f.c].Z) / 3;
                        if (zc - lo.Z < 0.05) flat += f.area;
                    }
                }
                var size = new Vec3(hi.X - lo.X, hi.Y - lo.Y, hi.Z - lo.Z + lift);
                rows.Add(new TiltRow { Tilt = tilt, Overhang = over, Flat = flat, Size = size, Fits = size.X <= BuildVolume.X && size.Y <= BuildVolume.Y && size.Z <= BuildVolume.Z });
            }
            Func<TiltRow, double> score = r => r.Fits ? r.Overhang + 3 * r.Flat : 1e12 + r.Overhang;
            return rows.OrderBy(score).ToList();
        }

        // ------------------------------------------------------------ automatic placement

        /// <summary>Triangles bucketed on an XY grid, to answer "is anything below this point?".</summary>
        private sealed class XyGrid
        {
            public readonly double X0, Y0, Cell;
            public readonly int Nx, Ny;
            public readonly Dictionary<int, List<int>> Cells = new Dictionary<int, List<int>>();

            public XyGrid(TriMesh m, Vec3[] v, double[][] box)
            {
                var span = 1.0;
                double lx = double.MaxValue, ly = double.MaxValue, hx = double.MinValue, hy = double.MinValue;
                foreach (var p in v)
                {
                    lx = Math.Min(lx, p.X); hx = Math.Max(hx, p.X); ly = Math.Min(ly, p.Y); hy = Math.Max(hy, p.Y);
                }
                if (v.Length == 0) { lx = ly = 0; hx = hy = 1; }
                span = Math.Max(Math.Max(hx - lx, hy - ly), 1);
                Cell = Math.Max(span / 128, 0.25);
                X0 = lx; Y0 = ly;
                Nx = (int)((hx - lx) / Cell) + 1;
                Ny = (int)((hy - ly) / Cell) + 1;
                for (var t = 0; t < box.Length; t++)
                {
                    int ix0 = Ix(box[t][0]), ix1 = Ix(box[t][3]), iy0 = Iy(box[t][1]), iy1 = Iy(box[t][4]);
                    for (var ix = ix0; ix <= ix1; ix++)
                        for (var iy = iy0; iy <= iy1; iy++)
                        {
                            var key = ix * Ny + iy;
                            if (!Cells.TryGetValue(key, out var list)) Cells[key] = list = new List<int>();
                            list.Add(t);
                        }
                }
            }

            public int Ix(double x) => Math.Min(Math.Max((int)Math.Floor((x - X0) / Cell), 0), Nx - 1);
            public int Iy(double y) => Math.Min(Math.Max((int)Math.Floor((y - Y0) / Cell), 0), Ny - 1);
        }

        /// <summary>
        /// Places as few supports as possible under overhanging faces. <paramref name="manual"/> supports are kept and count
        /// towards coverage. The result lists the kept manual supports followed by the new automatic ones.
        /// </summary>
        public static AutoResult Auto(TriMesh mesh, PrintParams print, AutoParams auto, IList<Support> manual, int nextId)
        {
            var frame = Frame(mesh, print.Tilt, print.Lift);
            if (frame == null) return null;
            var zoff = frame.Zoff;
            var v = mesh.Map(p => { var q = Tilt(p, print.Tilt); return new Vec3(q.X, q.Y, q.Z + zoff); }).Verts;
            const double tol = 0.3;

            var box = new double[mesh.TriCount][];
            for (var t = 0; t < box.Length; t++)
            {
                var b = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
                for (var k = 0; k < 3; k++)
                {
                    var q = v[mesh.Tris[t * 3 + k]];
                    b[0] = Math.Min(b[0], q.X); b[1] = Math.Min(b[1], q.Y); b[2] = Math.Min(b[2], q.Z);
                    b[3] = Math.Max(b[3], q.X); b[4] = Math.Max(b[4], q.Y); b[5] = Math.Max(b[5], q.Z);
                }
                box[t] = b;
            }
            var grid = new XyGrid(mesh, v, box);

            // would a pillar straight down from p cross the model?
            Func<Vec3, bool> blocked = p =>
            {
                if (!grid.Cells.TryGetValue(grid.Ix(p.X) * grid.Ny + grid.Iy(p.Y), out var list)) return false;
                foreach (var t in list)
                {
                    var b = box[t];
                    if (p.X < b[0] || p.X > b[3] || p.Y < b[1] || p.Y > b[4] || b[2] > p.Z - tol) continue;
                    Vec3 a0 = v[mesh.Tris[t * 3]], a1 = v[mesh.Tris[t * 3 + 1]], a2 = v[mesh.Tris[t * 3 + 2]];
                    double v0x = a1.X - a0.X, v0y = a1.Y - a0.Y, v1x = a2.X - a0.X, v1y = a2.Y - a0.Y;
                    double v2x = p.X - a0.X, v2y = p.Y - a0.Y;
                    var den = v0x * v1y - v1x * v0y;
                    if (Math.Abs(den) < 1e-12) continue;
                    var u = (v2x * v1y - v1x * v2y) / den;
                    var w = (v0x * v2y - v2x * v0y) / den;
                    if (u < -1e-9 || w < -1e-9 || u + w > 1 + 1e-9) continue;
                    var zh = a0.Z + u * (a1.Z - a0.Z) + w * (a2.Z - a0.Z);
                    if (zh < p.Z - tol) return true;
                }
                return false;
            };

            // area-weighted centre of the surface: faces looking at it are "inner" faces
            var centre = new Vec3(); double asum = 0;
            for (var t = 0; t < mesh.TriCount; t++)
            {
                Vec3 a = v[mesh.Tris[t * 3]], b = v[mesh.Tris[t * 3 + 1]], c = v[mesh.Tris[t * 3 + 2]];
                var l = (b - a).Cross(c - a).Length;
                centre = centre + (a + b + c) * (l / 3);
                asum += l;
            }
            if (asum > 0) centre = centre * (1 / asum);

            // samples on overhanging faces
            var ct = Math.Cos(auto.Angle * Math.PI / 180);
            var step = Math.Max(auto.Step, 0.05);
            var overhangFaces = 0;
            var samples = new List<KeyValuePair<double, Vec3>>();
            for (var t = 0; t < mesh.TriCount; t++)
            {
                Vec3 a = v[mesh.Tris[t * 3]], b = v[mesh.Tris[t * 3 + 1]], c = v[mesh.Tris[t * 3 + 2]];
                var cr = (b - a).Cross(c - a);
                var l = cr.Length;
                if (l < 1e-12 || cr.Z / l >= -ct) continue;
                overhangFaces++;
                Vec3 e1 = b - a, e2 = c - a;
                var m = Math.Max(Math.Max(e1.Length, e2.Length), (c - b).Length);
                var n = Math.Min((int)(m / step) + 1, 40);
                for (var a1 = 0; a1 < n; a1++)
                    for (var b1 = 0; b1 < n - a1; b1++)
                    {
                        var u = (a1 + 1.0 / 3) / n; var w = (b1 + 1.0 / 3) / n;
                        var p = a + e1 * u + e2 * w;
                        var d = p - centre;
                        var dd = d.Length;
                        var o = dd > 1e-9 ? d.Dot(cr) / (dd * l) : 0;
                        samples.Add(new KeyValuePair<double, Vec3>(p.Z + auto.Bias * (o + 1) / 2, p));
                    }
            }
            var ordered = samples.OrderBy(s => s.Key).ToList();

            // existing manual supports, in the print frame
            var placed = (manual ?? new List<Support>()).Select(s => { var p = Tilt(s.P, print.Tilt); return new Vec3(p.X, p.Y, p.Z + zoff); }).ToList();
            var kept = placed.Count;
            var sp2 = auto.Spacing * auto.Spacing;

            var result = new AutoResult { NextId = nextId };
            if (manual != null) result.Supports.AddRange(manual);
            int added = 0, blockedN = 0;
            foreach (var s in ordered)
            {
                var p = s.Value;
                var near = false;
                foreach (var q in placed)
                {
                    var d = p - q;
                    if (d.Dot(d) < sp2) { near = true; break; }
                }
                if (near) continue;
                if (blocked(p)) { blockedN++; continue; }
                placed.Add(p);
                added++;
                result.Supports.Add(new Support { Id = result.NextId++, P = Untilt(new Vec3(p.X, p.Y, p.Z - zoff), print.Tilt), Source = "auto" });
            }
            result.Stats = new AutoStats { OverhangFaces = overhangFaces, Samples = samples.Count, Added = added, Kept = kept, Blocked = blockedN };
            return result;
        }

        // ------------------------------------------------------------ pillars and raft

        private static List<Vec3> Circle(double cx, double cy, double r, int n)
        {
            var l = new List<Vec3>();
            for (var i = 0; i < n; i++)
            {
                var a = 2 * Math.PI * i / n;
                l.Add(new Vec3(cx + r * Math.Cos(a), cy + r * Math.Sin(a), 0));
            }
            return l;
        }

        /// <summary>Stadium (hull of two discs) around the segment p-q.</summary>
        private static List<Vec3> Stadium(double px, double py, double qx, double qy, double r)
        {
            var ang = Math.Atan2(qy - py, qx - px);
            const int half = 12;
            var poly = new List<Vec3>();
            for (var i = 0; i <= half; i++)
            {
                var a = ang - Math.PI / 2 + Math.PI * i / half;
                poly.Add(new Vec3(qx + r * Math.Cos(a), qy + r * Math.Sin(a), 0));
            }
            for (var i = 0; i <= half; i++)
            {
                var a = ang + Math.PI / 2 + Math.PI * i / half;
                poly.Add(new Vec3(px + r * Math.Cos(a), py + r * Math.Sin(a), 0));
            }
            return poly;
        }

        /// <summary>Pillars and raft for the supports, in the model frame (millimetres).</summary>
        public static SupportPlan Plan(TriMesh mesh, IList<Support> supports, PrintParams p)
        {
            var frame = Frame(mesh, p.Tilt, p.Lift);
            var plan = new SupportPlan { Up = Up(p.Tilt) };
            if (frame == null || supports.Count == 0) return plan;
            var zoff = frame.Zoff;
            // print frame -> model frame
            Func<double, double, double, Vec3> back = (x, y, z) => Untilt(new Vec3(x, y, z - zoff), p.Tilt);

            var pts = supports.Select(s => { var q = Tilt(s.P, p.Tilt); return new { S = s, Q = new Vec3(q.X, q.Y, q.Z + zoff), Tip = s.Tip ?? p.Tip }; }).ToList();
            var raftTop = Math.Max(Math.Min(p.Raft, p.Lift - 0.05), 0);
            var z0 = raftTop > 0 ? raftTop - 0.1 : 0;
            foreach (var s in pts)
            {
                var z1 = s.Q.Z + 0.3 * s.Tip; // the tip sinks slightly into the surface
                if (z1 - z0 <= 0.2) continue;
                plan.Pillars.Add(new Pillar { Support = s.S, Bottom = back(s.Q.X, s.Q.Y, z0), Top = back(s.Q.X, s.Q.Y, z1), BottomRadius = p.Base, TopRadius = s.Tip });
            }

            if (raftTop > 0 && pts.Count > 0)
            {
                // shortest links between supports (Prim's tree): one connected raft, little resin
                var n = pts.Count;
                var used = new bool[n];
                var d = Enumerable.Repeat(double.MaxValue, n).ToArray();
                var par = Enumerable.Repeat(-1, n).ToArray();
                d[0] = 0;
                for (var k = 0; k < n; k++)
                {
                    var b = -1;
                    for (var i = 0; i < n; i++)
                        if (!used[i] && (b < 0 || d[i] < d[b])) b = i;
                    used[b] = true;
                    var poly = par[b] < 0
                        ? Circle(pts[b].Q.X, pts[b].Q.Y, p.RaftRadius, 32)
                        : Stadium(pts[b].Q.X, pts[b].Q.Y, pts[par[b]].Q.X, pts[par[b]].Q.Y, p.RaftRadius);
                    plan.Raft.Add(new RaftPiece { Outline = poly.Select(o => back(o.X, o.Y, 0)).ToList(), Thickness = raftTop });
                    for (var i = 0; i < n; i++)
                    {
                        if (used[i]) continue;
                        var e = Math.Pow(pts[i].Q.X - pts[b].Q.X, 2) + Math.Pow(pts[i].Q.Y - pts[b].Q.Y, 2);
                        if (e < d[i]) { d[i] = e; par[i] = b; }
                    }
                }
            }
            return plan;
        }
    }
}
