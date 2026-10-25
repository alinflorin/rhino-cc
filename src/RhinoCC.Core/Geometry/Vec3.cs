using System;

namespace RhinoCC.Core.Geometry
{
    /// <summary>A plain 3D vector or point, so the printing logic does not need RhinoCommon.</summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z)
        {
            X = x; Y = y; Z = z;
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double k) => new Vec3(a.X * k, a.Y * k, a.Z * k);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double DistanceTo(Vec3 b) => (this - b).Length;

        public Vec3 Unit()
        {
            var l = Length;
            return l < 1e-12 ? this : new Vec3(X / l, Y / l, Z / l);
        }

        public override string ToString() => $"{X:0.###},{Y:0.###},{Z:0.###}";
    }

    /// <summary>An indexed triangle mesh: vertices and triples of vertex indices.</summary>
    public sealed class TriMesh
    {
        public Vec3[] Verts { get; }
        public int[] Tris { get; }

        public TriMesh(Vec3[] verts, int[] tris)
        {
            Verts = verts;
            Tris = tris;
        }

        public int TriCount => Tris.Length / 3;

        public void Tri(int t, out Vec3 a, out Vec3 b, out Vec3 c)
        {
            a = Verts[Tris[t * 3]];
            b = Verts[Tris[t * 3 + 1]];
            c = Verts[Tris[t * 3 + 2]];
        }

        /// <summary>The mesh with every point passed through <paramref name="f"/> (same triangles).</summary>
        public TriMesh Map(Func<Vec3, Vec3> f)
        {
            var v = new Vec3[Verts.Length];
            for (var i = 0; i < v.Length; i++) v[i] = f(Verts[i]);
            return new TriMesh(v, Tris);
        }

        public bool Bounds(out Vec3 lo, out Vec3 hi)
        {
            lo = hi = default;
            if (Verts.Length == 0) return false;
            lo = hi = Verts[0];
            foreach (var p in Verts)
            {
                lo = new Vec3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                hi = new Vec3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
            }
            return true;
        }

        /// <summary>Distance along the ray to the nearest triangle (Möller–Trumbore), skipping triangle <paramref name="skip"/>.</summary>
        public double? RayHit(Vec3 p, Vec3 d, int skip, double max)
        {
            double? best = null;
            for (var i = 0; i < TriCount; i++)
            {
                if (i == skip) continue;
                Tri(i, out var a, out var b, out var c);
                var e1 = b - a; var e2 = c - a;
                var pv = d.Cross(e2);
                var det = e1.Dot(pv);
                if (Math.Abs(det) < 1e-12) continue;
                var inv = 1 / det;
                var tv = p - a;
                var u = tv.Dot(pv) * inv;
                if (u < 0 || u > 1) continue;
                var q = tv.Cross(e1);
                var v = d.Dot(q) * inv;
                if (v < 0 || u + v > 1) continue;
                var t = e2.Dot(q) * inv;
                if (t > 1e-4 && t <= max && (!best.HasValue || t < best.Value)) best = t;
            }
            return best;
        }

        /// <summary>Closest point to <paramref name="p"/> on triangle abc (Ericson, Real-Time Collision Detection).</summary>
        public static Vec3 ClosestOnTri(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            var d1 = ab.Dot(ap); var d2 = ac.Dot(ap);
            if (d1 <= 0 && d2 <= 0) return a;
            var bp = p - b;
            var d3 = ab.Dot(bp); var d4 = ac.Dot(bp);
            if (d3 >= 0 && d4 <= d3) return b;
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            var d5 = ab.Dot(cp); var d6 = ac.Dot(cp);
            if (d6 >= 0 && d5 <= d6) return c;
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            var denom = 1 / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
}
