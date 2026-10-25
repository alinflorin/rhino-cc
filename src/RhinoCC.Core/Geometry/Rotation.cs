using System;

namespace RhinoCC.Core.Geometry
{
    /// <summary>3x3 rotation matrices as nine row-major numbers: parsing, checking, and snapping a frame to signed axes.</summary>
    public static class Rotation
    {
        public static double[] Parse(string text)
        {
            var parts = text.Split(',');
            if (parts.Length != 9) throw new ArgumentException($"'{text}' is not a 3x3 matrix: give nine numbers, row by row (r11,r12,r13,r21,...,r33)");
            var m = new double[9];
            for (var i = 0; i < 9; i++)
                if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out m[i]))
                    throw new ArgumentException($"'{parts[i]}' is not a number");
            return m;
        }

        /// <summary>A proper rotation: orthonormal rows and determinant +1 (within 1e-3).</summary>
        public static void RequireRotation(double[] m)
        {
            for (var i = 0; i < 3; i++)
                for (var j = i; j < 3; j++)
                {
                    var dot = m[i * 3] * m[j * 3] + m[i * 3 + 1] * m[j * 3 + 1] + m[i * 3 + 2] * m[j * 3 + 2];
                    if (Math.Abs(dot - (i == j ? 1 : 0)) > 1e-3) throw new ArgumentException("not a rotation matrix (rows must be orthonormal)");
                }
            if (Det(m) < 0) throw new ArgumentException("not a rotation matrix (determinant is -1, it mirrors)");
        }

        public static double Det(double[] m) =>
            m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);

        public static double[] Transpose(double[] m) => new[] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

        /// <summary>
        /// The signed axis permutation closest to a frame (x, y, z unit vectors), as three vectors, keeping it a rotation.
        /// zHint / xHint are "+z", "-x" ... : the world direction the frame's z / x axis must point to; null leaves it as found.
        /// </summary>
        public static double[][] SnapFrame(double[] x, double[] y, double[] z, string zHint, string xHint)
        {
            var zAxis = zHint != null ? Hint(zHint) : Nearest(z);
            var xAxis = xHint != null ? Hint(xHint) : Nearest(x);
            if (Math.Abs(Dot(zAxis, xAxis)) > 0.5)
            {
                // the hints agree on an axis: keep z, take x from what is left
                xAxis = NearestExcluding(x, zAxis);
            }
            var yAxis = Cross(zAxis, xAxis);
            return new[] { xAxis, yAxis, zAxis };
        }

        private static double[] Hint(string h)
        {
            if (h.Length != 2 || (h[0] != '+' && h[0] != '-')) throw new ArgumentException($"'{h}' is not a direction: use +x, -x, +y, -y, +z or -z");
            var s = h[0] == '+' ? 1.0 : -1.0;
            switch (char.ToLowerInvariant(h[1]))
            {
                case 'x': return new[] { s, 0, 0 };
                case 'y': return new[] { 0, s, 0 };
                case 'z': return new[] { 0, 0, s };
                default: throw new ArgumentException($"'{h}' is not a direction: use +x, -x, +y, -y, +z or -z");
            }
        }

        private static double[] Nearest(double[] v)
        {
            var k = 0;
            for (var i = 1; i < 3; i++) if (Math.Abs(v[i]) > Math.Abs(v[k])) k = i;
            var r = new double[3];
            r[k] = v[k] < 0 ? -1 : 1;
            return r;
        }

        private static double[] NearestExcluding(double[] v, double[] axis)
        {
            var w = new[] { v[0], v[1], v[2] };
            for (var i = 0; i < 3; i++) if (axis[i] != 0) w[i] = 0;
            return Nearest(w);
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
}
