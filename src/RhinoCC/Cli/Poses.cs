using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Geometry;

namespace RhinoCC.Cli
{
    /// <summary>
    /// An object's oriented box: a frame (origin at the box centre, x/y/z along its sides) and the side lengths.
    /// Found from the geometry, or pinned with `pose-rebase`; a pinned pose is kept in a user string and follows our own transforms.
    /// </summary>
    internal sealed class Pose
    {
        public Plane Frame;
        public Vector3d Size;
        public bool Pinned;

        public IEnumerable<Point3d> Corners()
        {
            var h = Size / 2;
            for (var i = 0; i < 8; i++)
                yield return Frame.Origin + Frame.XAxis * ((i & 1) == 0 ? -h.X : h.X) + Frame.YAxis * ((i & 2) == 0 ? -h.Y : h.Y) + Frame.ZAxis * ((i & 4) == 0 ? -h.Z : h.Z);
        }

        public void Transform(Transform x)
        {
            var f = Frame;
            f.Transform(x);
            var xa = f.XAxis;
            var ya = f.YAxis;
            xa.Unitize();
            ya.Unitize();
            Frame = new Plane(f.Origin, xa, ya);
            // sizes follow a uniform scale; shears and mirrors are not expected here
            Size *= Math.Pow(Math.Abs(x.Determinant), 1.0 / 3.0);
        }

        public IEnumerable<KeyValuePair<Line, Color>> Wire()
        {
            var c = Corners().ToArray();
            foreach (var e in new[] { 0, 1, 2, 3, 4, 5, 6, 7 }.SelectMany(i => new[] { new[] { i, i ^ 1 }, new[] { i, i ^ 2 }, new[] { i, i ^ 4 } }).Where(p => p[0] < p[1]))
                yield return new KeyValuePair<Line, Color>(new Line(c[e[0]], c[e[1]]), Color.Gold);
            var len = Math.Min(Size.X, Math.Min(Size.Y, Size.Z));
            if (len <= 0) len = Math.Max(Size.X, Math.Max(Size.Y, Size.Z));
            yield return new KeyValuePair<Line, Color>(new Line(Frame.Origin, Frame.Origin + Frame.XAxis * len), Color.Red);
            yield return new KeyValuePair<Line, Color>(new Line(Frame.Origin, Frame.Origin + Frame.YAxis * len), Color.Green);
            yield return new KeyValuePair<Line, Color>(new Line(Frame.Origin, Frame.Origin + Frame.ZAxis * len), Color.Blue);
        }
    }

    internal static class Poses
    {
        private const string Key = "RhinoCC.pose";
        private const int MaxPoints = 20000;

        // ---------------------------------------------------------------- finding

        public static Pose Get(RhinoObject o) => Stored(o) ?? Detect(o);

        /// <summary>The pinned pose, unless the object was moved behind our back (its box no longer matches what was stored).</summary>
        public static Pose Stored(RhinoObject o)
        {
            var s = o.Attributes.GetUserString(Key);
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                var n = s.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                if (n.Length != 18) return null;
                var box = o.Geometry.GetBoundingBox(true);
                var tol = 1e-6 * Math.Max(1, box.Diagonal.Length);
                if (box.Min.DistanceTo(new Point3d(n[12], n[13], n[14])) > tol || box.Max.DistanceTo(new Point3d(n[15], n[16], n[17])) > tol) return null;
                return new Pose
                {
                    Frame = new Plane(new Point3d(n[0], n[1], n[2]), new Vector3d(n[3], n[4], n[5]), new Vector3d(n[6], n[7], n[8])),
                    Size = new Vector3d(n[9], n[10], n[11]),
                    Pinned = true,
                };
            }
            catch (FormatException) { return null; }
        }

        public static void Save(RhinoDoc doc, Guid id, Pose p)
        {
            var o = doc.Objects.FindId(id);
            if (o == null) return;
            var box = o.Geometry.GetBoundingBox(true);
            var n = new[]
            {
                p.Frame.Origin.X, p.Frame.Origin.Y, p.Frame.Origin.Z, p.Frame.XAxis.X, p.Frame.XAxis.Y, p.Frame.XAxis.Z, p.Frame.YAxis.X, p.Frame.YAxis.Y, p.Frame.YAxis.Z,
                p.Size.X, p.Size.Y, p.Size.Z, box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z,
            };
            var attrs = o.Attributes.Duplicate();
            attrs.SetUserString(Key, string.Join(",", n.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
            doc.Objects.ModifyAttributes(o, attrs, true);
        }

        /// <summary>Transforms an object (or a copy of it) and carries a pinned pose along, so it stays valid.</summary>
        public static Guid Transform(RhinoDoc doc, Guid id, Transform x, bool copy)
        {
            var pinned = Stored(doc.Objects.FindId(id));
            var made = doc.Objects.Transform(id, x, !copy);
            if (pinned != null && made != Guid.Empty)
            {
                pinned.Transform(x);
                Save(doc, made, pinned);
            }
            return made;
        }

        // ---------------------------------------------------------------- measuring

        /// <summary>The box of the object in a given frame: the frame moved to the box centre, and the side lengths.</summary>
        public static Pose Measure(RhinoObject o, Plane frame)
        {
            var pts = Points(o);
            if (pts.Count == 0) throw new CliException("this object has no geometry to measure");
            return Measure(pts, frame);
        }

        /// <summary>
        /// The tightest box we can find: for a handful of candidate "up" directions (world axes, planar curve or face normals,
        /// principal axes) the smallest rectangle round the hull of the points seen from above, then the lowest volume wins.
        /// </summary>
        public static Pose Detect(RhinoObject o)
        {
            var pts = Points(o);
            if (pts.Count == 0) return Measure(o, Plane.WorldXY);
            var normals = new List<Vector3d> { Vector3d.ZAxis, Vector3d.XAxis, Vector3d.YAxis };
            normals.AddRange(Normals(o.Geometry));
            normals.AddRange(Principal(pts));

            Pose best = null;
            var bestVolume = double.MaxValue;
            var tried = new List<Vector3d>();
            foreach (var raw in normals)
            {
                var n = raw;
                if (!n.Unitize() || tried.Any(t => Math.Abs(t * n) > 0.99999)) continue;
                tried.Add(n);
                var p = Fit(pts, n);
                var volume = p.Size.X * p.Size.Y * p.Size.Z;
                // the world axes come first and win ties, so aligned parts report aligned boxes
                if (best == null || volume < bestVolume * 0.999) { best = p; bestVolume = volume; }
            }
            return best;
        }

        private static Pose Fit(List<Point3d> pts, Vector3d n)
        {
            var seed = new Plane(Point3d.Origin, n);
            var flat = pts.Select(p => (X: p.X * seed.XAxis.X + p.Y * seed.XAxis.Y + p.Z * seed.XAxis.Z, Y: p.X * seed.YAxis.X + p.Y * seed.YAxis.Y + p.Z * seed.YAxis.Z)).ToList();
            var hull = Hull2D.Hull(flat);
            double bestArea = double.MaxValue, bestAngle = 0;
            var angles = new List<double>();
            if (hull.Count < 3) angles.Add(0);
            else
                for (var i = 0; i < hull.Count; i++)
                {
                    var a = hull[i];
                    var b = hull[(i + 1) % hull.Count];
                    angles.Add(Math.Atan2(b.Y - a.Y, b.X - a.X));
                }
            var scored = angles.Select(t =>
            {
                double c = Math.Cos(t), s = Math.Sin(t), u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
                foreach (var h in hull.Count < 3 ? flat : hull)
                {
                    var u = h.X * c + h.Y * s;
                    var v = -h.X * s + h.Y * c;
                    u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                }
                return (Angle: t, Area: (u1 - u0) * (v1 - v0));
            }).ToList();
            bestArea = scored.Min(x => x.Area);
            // among equally tight rectangles prefer the one closest to the plane's own axes
            bestAngle = scored.Where(x => x.Area <= bestArea * 1.001 + 1e-12).OrderBy(x => Math.Abs(Math.IEEERemainder(x.Angle, Math.PI / 2))).First().Angle;

            var xa = seed.XAxis * Math.Cos(bestAngle) + seed.YAxis * Math.Sin(bestAngle);
            return Measure(pts, new Plane(Point3d.Origin, xa, Vector3d.CrossProduct(n, xa)));
        }

        private static Pose Measure(List<Point3d> pts, Plane frame)
        {
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            foreach (var p in pts)
            {
                var v = p - frame.Origin;
                var c = new[] { v * frame.XAxis, v * frame.YAxis, v * frame.ZAxis };
                for (var i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], c[i]); hi[i] = Math.Max(hi[i], c[i]); }
            }
            var centre = frame.Origin + frame.XAxis * ((lo[0] + hi[0]) / 2) + frame.YAxis * ((lo[1] + hi[1]) / 2) + frame.ZAxis * ((lo[2] + hi[2]) / 2);
            return new Pose { Frame = new Plane(centre, frame.XAxis, frame.YAxis), Size = new Vector3d(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]) };
        }

        // ---------------------------------------------------------------- samples

        /// <summary>Points that stand for the object's shape: mesh vertices, curve samples, the point itself.</summary>
        public static List<Point3d> Points(RhinoObject o)
        {
            var pts = new List<Point3d>();
            var g = o.Geometry;
            if (g is Extrusion e) g = e.ToBrep(true);
            if (g is Brep b)
            {
                var meshes = Mesh.CreateFromBrep(b, MeshingParameters.FastRenderMesh);
                if (meshes != null) foreach (var m in meshes) pts.AddRange(m.Vertices.ToPoint3dArray());
                pts.AddRange(b.Vertices.Select(v => v.Location));
            }
            else if (g is Mesh mesh) pts.AddRange(mesh.Vertices.ToPoint3dArray());
            else if (g is Curve c)
            {
                if (c.TryGetPolyline(out var pl)) pts.AddRange(pl);
                else
                {
                    var t = c.DivideByCount(Math.Max(8, (int)Math.Min(400, c.GetLength() > 0 ? 200 : 8)), true);
                    if (t != null) pts.AddRange(t.Select(c.PointAt));
                    pts.Add(c.PointAtStart);
                    pts.Add(c.PointAtEnd);
                }
            }
            else if (g is Rhino.Geometry.Point pt) pts.Add(pt.Location);
            else if (g is Surface s)
            {
                var br = s.ToBrep();
                if (br != null) { var meshes = Mesh.CreateFromBrep(br, MeshingParameters.FastRenderMesh); if (meshes != null) foreach (var m in meshes) pts.AddRange(m.Vertices.ToPoint3dArray()); }
            }
            else
            {
                var box = g.GetBoundingBox(true);
                if (box.IsValid) pts.AddRange(box.GetCorners());
            }
            if (pts.Count > MaxPoints)
            {
                var step = (pts.Count + MaxPoints - 1) / MaxPoints;
                pts = pts.Where((_, i) => i % step == 0).ToList();
            }
            return pts;
        }

        /// <summary>Directions that often are "up" for the object: the plane of a flat curve, the normals of its largest flat faces.</summary>
        private static IEnumerable<Vector3d> Normals(GeometryBase g)
        {
            if (g is Extrusion e) g = e.ToBrep(true);
            if (g is Curve c && c.TryGetPlane(out var plane)) yield return plane.ZAxis;
            if (g is Brep b)
            {
                var faces = new List<KeyValuePair<double, Vector3d>>();
                foreach (var f in b.Faces)
                {
                    if (!f.TryGetPlane(out var fp, 0.01)) continue;
                    var area = AreaMassProperties.Compute(f.DuplicateFace(false))?.Area ?? 0;
                    faces.Add(new KeyValuePair<double, Vector3d>(area, fp.ZAxis));
                }
                foreach (var f in faces.OrderByDescending(x => x.Key).Take(5)) yield return f.Value;
            }
        }

        /// <summary>The three principal directions of the points (power iteration on the covariance), for round or leaning shapes.</summary>
        private static IEnumerable<Vector3d> Principal(List<Point3d> pts)
        {
            var mean = new Vector3d(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in pts)
            {
                var d = new Vector3d(p.X, p.Y, p.Z) - mean;
                xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
            }
            Vector3d Mul(Vector3d v) => new Vector3d(xx * v.X + xy * v.Y + xz * v.Z, xy * v.X + yy * v.Y + yz * v.Z, xz * v.X + yz * v.Y + zz * v.Z);
            var found = new List<Vector3d>();
            foreach (var seed in new[] { new Vector3d(1, 0.31, 0.17), new Vector3d(0.2, 1, 0.43), new Vector3d(0.37, 0.11, 1) })
            {
                var v = seed;
                for (var i = 0; i < 40; i++)
                {
                    foreach (var f in found) v -= f * (v * f);
                    v = Mul(v);
                    if (!v.Unitize()) break;
                }
                if (v.IsValid && v.Length > 0.5) { found.Add(v); yield return v; }
            }
        }
    }
}
