using System;
using System.Collections.Generic;

namespace RhinoCC.Core.Printing
{
    /// <summary>A triangle of the piece: outward unit normal, area and centroid.</summary>
    public struct Facet
    {
        public double Nx, Ny, Nz, Area, Cx, Cy, Cz;

        public Facet(double nx, double ny, double nz, double area, double cx, double cy, double cz)
        {
            Nx = nx; Ny = ny; Nz = nz; Area = area; Cx = cx; Cy = cy; Cz = cz;
        }
    }

    public sealed class TiltResult
    {
        public string Axis { get; set; }
        public double Degrees { get; set; }
        /// <summary>Share (0-1) of the surface area that needs support.</summary>
        public double Fraction { get; set; }
    }

    /// <summary>Which faces need support when the piece is printed with +Z up, optionally tilted about X or Y.</summary>
    public static class Overhang
    {
        /// <summary>
        /// Share of the area facing down within <paramref name="angleDeg"/> of straight down (45 = the usual limit).
        /// Faces lying on the plate (lowest point, flat) need none. A screen on triangle normals, not a slicer.
        /// </summary>
        public static double Fraction(IReadOnlyList<Facet> faces, double tiltDeg, string axis, double angleDeg)
        {
            if (faces.Count == 0) return 0;
            var t = tiltDeg * Math.PI / 180.0;
            double c = Math.Cos(t), s = Math.Sin(t);
            var limit = -Math.Cos(angleDeg * Math.PI / 180.0);
            var rotX = string.Equals(axis, "x", StringComparison.OrdinalIgnoreCase);

            var minZ = double.MaxValue;
            var z = new double[faces.Count];
            var nz = new double[faces.Count];
            for (var i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                // rotation about X turns (y,z); about Y turns (z,x)
                z[i] = rotX ? s * f.Cy + c * f.Cz : c * f.Cz - s * f.Cx;
                nz[i] = rotX ? s * f.Ny + c * f.Nz : c * f.Nz - s * f.Nx;
                if (z[i] < minZ) minZ = z[i];
            }

            double total = 0, bad = 0;
            for (var i = 0; i < faces.Count; i++)
            {
                total += faces[i].Area;
                var onPlate = nz[i] < -0.999 && z[i] - minZ < 0.05;
                if (nz[i] < limit && !onPlate) bad += faces[i].Area;
            }
            return total > 0 ? bad / total : 0;
        }

        /// <summary>Overhang share for tilts -maxDeg..maxDeg about X and Y, best first (ties: smaller tilt).</summary>
        public static List<TiltResult> TiltSearch(IReadOnlyList<Facet> faces, double angleDeg, double stepDeg = 5, double maxDeg = 60)
        {
            var rows = new List<TiltResult>();
            foreach (var axis in new[] { "x", "y" })
                for (var d = -maxDeg; d <= maxDeg + 1e-9; d += stepDeg)
                    rows.Add(new TiltResult { Axis = axis, Degrees = d, Fraction = Fraction(faces, d, axis, angleDeg) });
            rows.Sort((a, b) => a.Fraction != b.Fraction ? a.Fraction.CompareTo(b.Fraction) : Math.Abs(a.Degrees).CompareTo(Math.Abs(b.Degrees)));
            return rows;
        }
    }
}
