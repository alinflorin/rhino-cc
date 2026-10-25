using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Printing;

namespace RhinoCC.Cli
{
    /// <summary>`analyze`: checks the Rhino geometry itself (breps, extrusions, meshes), not an exported file.</summary>
    internal static class AnalyzeCommand
    {
        private const int MaxObjects = 40;
        private const int MaxWallSamples = 1500;

        public static void Register(RhinoCli cli)
        {
            cli.Register(new CommandSpec("analyze", "Analysis",
                "analyze [ID... | --selected | --layer NAME | --name NAME] [--metal NAME] [--min-wall MM] [--angle DEG] [--no-walls] [--no-overhang]",
                "Checks the model's own solids for casting and printing: validity and closedness (open edges, flipped orientation), volume, weight in a metal, size against the printer's build volume, thinnest walls (with where) and overhangs with the best tilt. Without targets: every visible brep, extrusion and mesh. Wall and overhang figures are samples, a screen, not a proof.", a =>
            {
                var doc = RhinoCommands.Doc();
                var metal = a.Opt("metal") ?? "sterling";
                double density;
                try { density = Metals.Lookup(metal); }
                catch (ArgumentException e) { throw new CliException(e.Message); }
                var minWall = a.OptNumber("min-wall") ?? 0.8;
                var angle = a.OptNumber("angle") ?? 45;
                if (minWall <= 0) throw new CliException("--min-wall must be positive");
                if (angle <= 0 || angle >= 90) throw new CliException("--angle must be between 0 and 90");
                var walls = !a.Has("no-walls");
                var overhang = !a.Has("no-overhang");
                var toMm = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);

                var objects = Select(doc, a);
                if (objects.Count == 0) throw new CliException("nothing to analyze: no visible brep, extrusion or mesh objects");

                var sb = new StringBuilder();
                sb.AppendLine($"analyze: {Math.Min(objects.Count, MaxObjects)} object(s), sizes in mm, weight as {metal} ({Fmt.N(density)} g/cm³), model units {doc.ModelUnitSystem}");
                if (objects.Count > MaxObjects) sb.AppendLine($"{objects.Count} objects: analyzing the first {MaxObjects}; name or select the rest");
                var problems = new List<string>();
                double totalVol = 0;
                foreach (var o in objects.Take(MaxObjects))
                {
                    var tag = $"{Fmt.Short(o.Id)}{(string.IsNullOrEmpty(o.Name) ? "" : " " + o.Name)}";
                    sb.AppendLine($"{Fmt.Short(o.Id)} {o.ObjectType}{(string.IsNullOrEmpty(o.Name) ? "" : $" \"{o.Name}\"")}");
                    var mine = new List<string>();
                    try { totalVol += One(o, toMm, density, minWall, angle, walls, overhang, sb, mine); }
                    catch (Exception e) { sb.AppendLine($"  analysis failed: {e.Message}"); mine.Add("analysis failed"); }
                    problems.AddRange(mine.Select(m => $"{tag}: {m}"));
                }
                if (totalVol > 0) sb.AppendLine($"total: volume {Fmt.N(totalVol)} mm³, {Fmt.N(Metals.Grams(totalVol, density))} g {metal}");
                sb.Append(problems.Count == 0 ? "no problems found" : "problems:\n  " + string.Join("\n  ", problems));
                return CliResult.Ok(sb.ToString());
            }).Opt("metal", "NAME", "Metal for the weight (default sterling): " + Metals.Names + ".")
              .Opt("min-wall", "MM", "Thinnest acceptable wall in mm (default 0.8).")
              .Opt("angle", "DEG", "Overhang limit: faces within DEG of facing straight down need support (default 45).")
              .Flag("no-walls", "Skip the wall-thickness check (the slow one).").Flag("no-overhang", "Skip the overhang check.")
              .Flag("selected", "Analyze the current selection.").Opt("layer", "NAME", "Only this layer (full path).").Opt("name", "NAME", "Only objects with this exact name.")
              .Example("analyze --selected --metal 18k")
              .Example("analyze --layer shank --min-wall 1"));
        }

        internal static List<RhinoObject> Select(RhinoDoc doc, CliArgs a)
        {
            IEnumerable<RhinoObject> all;
            if (a.Positional.Count > 0) all = Fmt.Ids(doc, a.Positional).Select(id => doc.Objects.FindId(id));
            else if (a.Has("selected")) all = doc.Objects.GetSelectedObjects(false, false);
            else all = doc.Objects.Where(o => !o.IsHidden);
            if (a.Has("layer"))
                all = all.Where(o => o.Attributes.LayerIndex >= 0 && doc.Layers[o.Attributes.LayerIndex].FullPath.Equals(a.Opt("layer"), StringComparison.OrdinalIgnoreCase));
            if (a.Has("name"))
                all = all.Where(o => string.Equals(o.Name, a.Opt("name"), StringComparison.OrdinalIgnoreCase));
            return all.Where(o => o.Geometry is Brep || o.Geometry is Extrusion || o.Geometry is Mesh).ToList();
        }

        /// <summary>Writes the report lines of one object; returns its volume (mm³) when it is a closed solid, else 0.</summary>
        private static double One(RhinoObject o, double toMm, double density, double minWall, double angle, bool walls, bool overhang, StringBuilder sb, List<string> problems)
        {
            var g = o.Geometry;
            Mesh mesh = null;
            bool solid;
            int orientation; // 1 outward, -1 inward, 0 unknown or open
            double volume = 0;
            var health = new List<string>();

            if (g is Mesh m)
            {
                solid = m.IsClosed;
                orientation = solid ? m.SolidOrientation() : 0;
                if (!m.IsValid) health.Add("invalid mesh");
                if (!solid) health.Add("open mesh (holes or open edges)");
                else if (!m.IsManifold(true, out _, out _)) health.Add("not manifold (edges shared by more than two faces)");
                if (solid && orientation != 0) volume = Math.Abs(VolumeMassProperties.Compute(m)?.Volume ?? 0);
                mesh = m.DuplicateMesh();
            }
            else
            {
                var b = g as Brep ?? ((Extrusion)g).ToBrep(true);
                if (!b.IsValid)
                {
                    b.IsValidWithLog(out var log);
                    health.Add("invalid geometry: " + (log ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0));
                }
                solid = b.IsSolid;
                var naked = b.Edges.Count(e => e.Valence == EdgeAdjacency.Naked);
                var nonManifold = b.Edges.Count(e => e.Valence == EdgeAdjacency.NonManifold);
                if (!solid) health.Add(naked > 0 ? $"open: {naked} naked edge(s), so not a solid (join or cap it)" : "not a solid");
                if (nonManifold > 0) health.Add($"{nonManifold} non-manifold edge(s)");
                orientation = solid ? (b.SolidOrientation == BrepSolidOrientation.Inward ? -1 : 1) : 0;
                if (solid) volume = Math.Abs(VolumeMassProperties.Compute(b)?.Volume ?? 0);
                if (solid && (walls || overhang))
                {
                    var parts = Mesh.CreateFromBrep(b, MeshingParameters.Default);
                    if (parts != null && parts.Length > 0)
                    {
                        mesh = new Mesh();
                        mesh.Append(parts);
                    }
                }
            }
            if (solid && orientation < 0) health.Add("normals point inward (flip the object's direction)");
            problems.AddRange(health);

            sb.AppendLine($"  {(health.Count == 0 ? "closed valid solid" : "health: " + string.Join("; ", health))}");

            var d = g.GetBoundingBox(true).Diagonal;
            var dx = d.X * toMm; var dy = d.Y * toMm; var dz = d.Z * toMm;
            var fit = BuildVolume.FitsAsIs(dx, dy, dz) ? "fits the build volume" : BuildVolume.Fits(dx, dy, dz) ? "fits the build volume only if turned" : "DOES NOT FIT the build volume";
            sb.AppendLine($"  size {Fmt.N(dx)} x {Fmt.N(dy)} x {Fmt.N(dz)} mm ({fit}: {Fmt.N(BuildVolume.X)} x {Fmt.N(BuildVolume.Y)} x {Fmt.N(BuildVolume.Z)})");
            if (!BuildVolume.Fits(dx, dy, dz)) problems.Add("too large for the printer");

            double vol = 0;
            if (solid && volume > 0)
            {
                vol = volume * toMm * toMm * toMm;
                sb.AppendLine($"  volume {Fmt.N(vol)} mm³, weight {Fmt.N(Metals.Grams(vol, density))} g");
            }
            else if (solid) sb.AppendLine("  volume: could not be computed");
            else sb.AppendLine("  volume and weight: only for a closed solid");

            if (mesh == null || !solid || orientation == 0)
            {
                if (walls || overhang) sb.AppendLine("  walls and overhangs: need a closed solid");
                return vol;
            }

            mesh.Faces.ConvertQuadsToTriangles();
            mesh.FaceNormals.ComputeFaceNormals();
            if (orientation < 0) mesh.Flip(true, true, true);
            var tris = Triangles(mesh);

            if (walls)
            {
                var wl = Walls(mesh, tris, toMm, minWall);
                if (wl == null) sb.AppendLine("  walls: no samples");
                else
                {
                    sb.AppendLine($"  walls: thinnest {Fmt.N(wl.Min)} mm (sampled {wl.Count} points), {Fmt.N(wl.Share * 100)}% of samples under {Fmt.N(minWall)} mm");
                    if (wl.Where.Count > 0) sb.AppendLine("  thin spots: " + string.Join("; ", wl.Where.Select(w => $"{Fmt.N(w.Item1)} mm at {Fmt.P(w.Item2)}")));
                    if (wl.Min < minWall) problems.Add($"wall down to {Fmt.N(wl.Min)} mm, under {Fmt.N(minWall)} mm");
                }
            }

            if (overhang)
            {
                var facets = tris.Select(t => t.Facet).ToList();
                var now = Overhang.Fraction(facets, 0, "x", angle);
                var best = Overhang.TiltSearch(facets, angle)[0];
                sb.AppendLine($"  overhang (as modelled, +Z up, within {Fmt.N(angle)}° of facing down): {Fmt.N(now * 100)}% of the surface" +
                    (best.Fraction + 0.01 < now ? $"; best tilt {Fmt.N(best.Degrees)}° about {best.Axis.ToUpperInvariant()}: {Fmt.N(best.Fraction * 100)}%" : "; no tilt up to 60° is clearly better"));
            }
            return vol;
        }

        private sealed class Tri
        {
            public Facet Facet;
            public Point3d Centroid;
            public Vector3d Normal;
        }

        private static List<Tri> Triangles(Mesh mesh)
        {
            var list = new List<Tri>(mesh.Faces.Count);
            for (var i = 0; i < mesh.Faces.Count; i++)
            {
                var f = mesh.Faces[i];
                var a = mesh.Vertices[f.A]; var b = mesh.Vertices[f.B]; var c = mesh.Vertices[f.C];
                var cross = Vector3d.CrossProduct(new Vector3d(b.X - a.X, b.Y - a.Y, b.Z - a.Z), new Vector3d(c.X - a.X, c.Y - a.Y, c.Z - a.Z));
                var area = cross.Length / 2;
                if (area < 1e-12) continue;
                var n = cross;
                n.Unitize();
                var ctr = new Point3d((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                list.Add(new Tri { Facet = new Facet(n.X, n.Y, n.Z, area, ctr.X, ctr.Y, ctr.Z), Centroid = ctr, Normal = n });
            }
            return list;
        }

        private sealed class WallReport
        {
            public double Min;
            public int Count;
            public double Share;
            public List<Tuple<double, Point3d>> Where = new List<Tuple<double, Point3d>>();
        }

        /// <summary>Shoots a ray into the metal from sampled triangles; the first wall it meets is the local thickness.</summary>
        private static WallReport Walls(Mesh mesh, List<Tri> tris, double toMm, double minWall)
        {
            if (tris.Count == 0) return null;
            var step = Math.Max(1, tris.Count / MaxWallSamples);
            var eps = 0.001 / toMm;
            var found = new List<Tuple<double, Point3d>>();
            for (var i = 0; i < tris.Count; i += step)
            {
                var t = tris[i];
                var dir = -t.Normal;
                var hit = Intersection.MeshRay(mesh, new Ray3d(t.Centroid + dir * eps, dir));
                if (hit > 0) found.Add(Tuple.Create((hit + eps) * toMm, t.Centroid));
            }
            if (found.Count == 0) return null;
            var thin = found.Where(f => f.Item1 < minWall).OrderBy(f => f.Item1).ToList();
            var report = new WallReport { Min = found.Min(f => f.Item1), Count = found.Count, Share = (double)thin.Count / found.Count };
            // a few distinct places, not many samples of the same one
            var apart = 3.0 / toMm;
            foreach (var f in thin)
            {
                if (report.Where.Count >= 3) break;
                if (report.Where.All(w => w.Item2.DistanceTo(f.Item2) > apart)) report.Where.Add(f);
            }
            return report;
        }
    }
}
