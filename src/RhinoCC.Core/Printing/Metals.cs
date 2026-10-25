using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoCC.Core.Printing
{
    /// <summary>Densities (g/cm³) of the usual jewelry metals and the printer's build volume; the numbers `analyze` reasons with.</summary>
    public static class Metals
    {
        public static readonly IReadOnlyDictionary<string, double> Density = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["sterling"] = 10.4,
            ["silver"] = 10.5,
            ["bronze"] = 8.8,
            ["brass"] = 8.5,
            ["10k"] = 11.6,
            ["14k"] = 13.0,
            ["14k-white"] = 12.9,
            ["14k-rose"] = 13.0,
            ["18k"] = 15.5,
            ["18k-white"] = 14.7,
            ["18k-rose"] = 15.0,
            ["platinum"] = 21.4,
            ["palladium"] = 12.0,
            ["resin"] = 1.15,
        };

        public static string Names => string.Join(", ", Density.Keys);

        /// <summary>Grams for a volume in mm³.</summary>
        public static double Grams(double volumeMm3, double density) => volumeMm3 / 1000.0 * density;

        public static double Lookup(string metal)
        {
            if (Density.TryGetValue(metal ?? "", out var d)) return d;
            throw new ArgumentException($"unknown metal '{metal}' (known: {Names})");
        }
    }

    /// <summary>Elegoo Mars 5 build volume (mm).</summary>
    public static class BuildVolume
    {
        public const double X = 153.4, Y = 77, Z = 150;

        /// <summary>True when a box of this size fits in some axis-aligned orientation (a piece can be laid or stood up).</summary>
        public static bool Fits(double dx, double dy, double dz)
        {
            var part = new[] { dx, dy, dz }.OrderBy(v => v).ToArray();
            var room = new[] { X, Y, Z }.OrderBy(v => v).ToArray();
            return part[0] <= room[0] && part[1] <= room[1] && part[2] <= room[2];
        }

        /// <summary>True when it fits as modelled (x along X, and so on).</summary>
        public static bool FitsAsIs(double dx, double dy, double dz) => dx <= X && dy <= Y && dz <= Z;
    }
}
