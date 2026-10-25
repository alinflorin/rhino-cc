using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoCC.Core.Cli;

namespace RhinoCC.Cli
{
    /// <summary>Parsing and printing of the things commands pass around: points, ids, numbers.</summary>
    internal static class Fmt
    {
        public static string N(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

        public static string P(Point3d p) => $"{N(p.X)},{N(p.Y)},{N(p.Z)}";

        public static string P(Vector3d p) => $"{N(p.X)},{N(p.Y)},{N(p.Z)}";

        public static Point3d Point(string text, string what)
        {
            var parts = text.Split(',');
            if (parts.Length < 2 || parts.Length > 3) throw new CliException($"{what}: '{text}' is not a point (x,y,z)");
            return new Point3d(CliArgs.Number(parts[0], what), CliArgs.Number(parts[1], what), parts.Length == 3 ? CliArgs.Number(parts[2], what) : 0);
        }

        public static Vector3d Vector(string text, string what) => new Vector3d(Point(text, what));

        public static System.Drawing.Color Color(string text, string what)
        {
            var parts = text.Split(',');
            if (parts.Length != 3 || !parts.All(x => int.TryParse(x, out var v) && v >= 0 && v <= 255))
                throw new CliException($"{what}: '{text}' is not a colour (r,g,b with 0-255)");
            return System.Drawing.Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        }

        public static string Box(BoundingBox b) => b.IsValid ? $"[{P(b.Min)} .. {P(b.Max)}]" : "[none]";

        /// <summary>Full ids, or a unique prefix of one; ids are 36 characters and agents should not have to repeat them.</summary>
        public static List<Guid> Ids(RhinoDoc doc, IEnumerable<string> tokens)
        {
            var all = doc.Objects.Select(o => o.Id).ToList();
            var ids = new List<Guid>();
            foreach (var t in tokens)
            {
                if (Guid.TryParse(t, out var g))
                {
                    if (doc.Objects.FindId(g) == null) throw new CliException($"no object {t}");
                    ids.Add(g);
                    continue;
                }
                var hits = all.Where(x => x.ToString().StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
                if (t.Length < 4 || hits.Count == 0) throw new CliException($"no object {t}");
                if (hits.Count > 1) throw new CliException($"'{t}' matches {hits.Count} objects; use more characters");
                ids.Add(hits[0]);
            }
            if (ids.Count == 0) throw new CliException("missing object id(s)");
            return ids;
        }

        public static string Short(Guid id) => id.ToString().Substring(0, 8);
    }
}
