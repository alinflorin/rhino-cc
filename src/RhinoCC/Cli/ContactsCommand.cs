using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Geometry;

namespace RhinoCC.Cli
{
    /// <summary>`contacts`: which parts touch, overlap or almost touch, where, over how much surface, and which parts hang together.</summary>
    internal static class ContactsCommand
    {
        private const int MaxObjects = 60;
        private const int MaxSamples = 300;

        private sealed class Body
        {
            public RhinoObject Obj;
            public string Label;
            public BoundingBox Box;
            public Brep Brep;
            public Mesh Mesh;
            public List<Point3d> Samples;
            public bool Closed;

            public Point3d Closest(Point3d p) => Brep != null ? Brep.ClosestPoint(p) : Mesh.ClosestPoint(p);

            public bool Inside(Point3d p, double tol) =>
                Closed && (Brep != null ? Brep.IsPointInside(p, tol, true) : Mesh.IsPointInside(p, tol, true));
        }

        private sealed class Link
        {
            public int A, B;
            public string Kind; // contact, overlap, gap
            public double Gap;  // mm
            public double Area; // mm², face contacts
            public double Length; // mm, line contacts
            public Point3d At;
            public Vector3d Normal;
            public int Inside;
        }

        public static void Register(RhinoCli cli)
        {
            cli.Register(new CommandSpec("contacts", "Analysis",
                "contacts [ID... | --selected | --layer NAME | --name NAME] [--tol MM] [--near MM] [--show | --hide]",
                "Which solids touch: for every pair that is close, a face contact (area, place, normal), a line or point contact, an overlap (one runs into the other) or a small gap, then the groups of parts that hang together and the loose ones. Use it to check that stones sit on their seats, prongs reach the stone, and nothing floats. Areas are estimates from sample points. Without targets: every visible brep, extrusion and mesh.", a =>
            {
                var doc = RhinoCommands.Doc();
                var toMm = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
                if (a.Has("hide")) { Overlay.Graph.Clear(); doc.Views.Redraw(); return CliResult.Ok("graph hidden"); }
                var tolMm = a.OptNumber("tol") ?? 0.02;
                var nearMm = a.OptNumber("near") ?? 0.3;
                if (tolMm <= 0 || nearMm < tolMm) throw new CliException("--tol must be positive and --near at least as big");
                var tol = tolMm / toMm;
                var near = nearMm / toMm;

                var objects = AnalyzeCommand.Select(doc, a);
                if (objects.Count < 2) throw new CliException("need at least two brep, extrusion or mesh objects");
                var note = objects.Count > MaxObjects ? $"{objects.Count} objects: using the first {MaxObjects}; name or select the rest\n" : "";
                var bodies = objects.Take(MaxObjects).Select(Prepare).Where(b => b != null).ToList();

                var links = new List<Link>();
                for (var i = 0; i < bodies.Count; i++)
                    for (var j = i + 1; j < bodies.Count; j++)
                    {
                        var box = bodies[i].Box;
                        box.Inflate(near);
                        if (!BoundingBox.Intersection(box, bodies[j].Box).IsValid) continue;
                        var link = Pair(bodies[i], bodies[j], i, j, tol, near, toMm);
                        if (link != null) links.Add(link);
                    }

                var sb = new StringBuilder(note);
                sb.AppendLine($"contacts: {bodies.Count} object(s), touching within {Fmt.N(tolMm)} mm, gaps reported up to {Fmt.N(nearMm)} mm, areas in mm²");
                foreach (var l in links.OrderBy(l => l.Kind == "gap" ? 1 : 0).ThenBy(l => l.A).ThenBy(l => l.B))
                {
                    var pair = $"{bodies[l.A].Label} ~ {bodies[l.B].Label}";
                    switch (l.Kind)
                    {
                        case "gap": sb.AppendLine($"  {pair}: gap {Fmt.N(l.Gap)} mm near {Fmt.P(l.At)}"); break;
                        case "overlap": sb.AppendLine($"  {pair}: overlap, {l.Inside} sample point(s) of one lie inside the other, near {Fmt.P(l.At)}"); break;
                        default:
                            var how = l.Area > 0 ? $"face, about {Fmt.N(l.Area)} mm²" : l.Length > 0 ? $"line, about {Fmt.N(l.Length)} mm long" : "point";
                            sb.AppendLine($"  {pair}: contact ({how}) at {Fmt.P(l.At)}, normal {Fmt.P(l.Normal)}");
                            break;
                    }
                }
                if (links.Count == 0) sb.AppendLine("  nothing touches or comes near");

                var joined = links.Where(l => l.Kind != "gap").Select(l => (l.A, l.B));
                var groups = Components.Of(bodies.Count, joined);
                sb.AppendLine($"{groups.Count} group(s) of parts that touch or overlap:");
                foreach (var g in groups) sb.AppendLine("  " + (g.Count == 1 ? "loose: " : "") + string.Join(", ", g.Select(i => bodies[i].Label)));
                if (groups.Count > 1) sb.Append("(parts in different groups are separate pieces)");

                if (a.Has("show"))
                {
                    Overlay.Graph.Set(Wires(bodies, links));
                    doc.Views.Redraw();
                    sb.AppendLine().Append("graph drawn: green contact, red overlap, orange gap; `contacts --hide` removes it");
                }
                return CliResult.Ok(sb.ToString().TrimEnd());
            }).Opt("tol", "MM", "Distance counted as touching (default 0.02).").Opt("near", "MM", "Report gaps up to this size (default 0.3).")
              .Flag("selected", "The current selection.").Opt("layer", "NAME", "Only this layer.").Opt("name", "NAME", "Only objects with this name.")
              .Flag("show", "Also draw the graph over the model (for `shot`).").Flag("hide", "Remove the drawn graph."));
        }

        private static Body Prepare(RhinoObject o)
        {
            var b = new Body { Obj = o, Label = string.IsNullOrEmpty(o.Name) ? Fmt.Short(o.Id) : o.Name };
            var pts = Poses.Points(o);
            if (pts.Count == 0) return null;
            if (o.Geometry is Mesh m)
            {
                b.Mesh = m;
                b.Closed = m.IsClosed;
            }
            else
            {
                b.Brep = o.Geometry as Brep ?? ((Extrusion)o.Geometry).ToBrep(true);
                b.Closed = b.Brep.IsSolid;
            }
            b.Box = o.Geometry.GetBoundingBox(true);
            var step = Math.Max(1, pts.Count / MaxSamples);
            b.Samples = pts.Where((_, i) => i % step == 0).ToList();
            return b;
        }

        private static Link Pair(Body a, Body b, int ia, int ib, double tol, double near, double toMm)
        {
            var touch = new List<Point3d>();
            double min = double.MaxValue;
            var at = Point3d.Unset;
            var inside = 0;
            foreach (var (from, to) in new[] { (a, b), (b, a) })
                foreach (var p in from.Samples)
                {
                    var q = to.Closest(p);
                    var d = p.DistanceTo(q);
                    if (d < min) { min = d; at = new Point3d((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2); }
                    if (d <= tol) touch.Add(new Point3d((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2));
                    else if (d > near * 4 && !to.Box.Contains(p)) continue;
                    else if (to.Inside(p, tol)) inside++;
                }
            if (inside > 0) return new Link { A = ia, B = ib, Kind = "overlap", Inside = inside, At = at };
            if (touch.Count > 0) return Contact(a, b, ia, ib, touch, toMm);
            if (min <= near) return new Link { A = ia, B = ib, Kind = "gap", Gap = min * toMm, At = at };
            return null;
        }

        /// <summary>Size and place of a contact from the points where the two surfaces meet.</summary>
        private static Link Contact(Body a, Body b, int ia, int ib, List<Point3d> pts, double toMm)
        {
            var centre = new Point3d(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
            var link = new Link { A = ia, B = ib, Kind = "contact", At = centre };
            var towards = b.Box.Center - a.Box.Center;
            if (pts.Count >= 3 && Plane.FitPlaneToPoints(pts, out var plane) == PlaneFitResult.Success)
            {
                var n = plane.ZAxis;
                if (n * towards < 0) n = -n;
                link.Normal = n;
                var flat = pts.Select(p => (X: (p - centre) * plane.XAxis, Y: (p - centre) * plane.YAxis)).ToList();
                var hull = Hull2D.Hull(flat);
                var area = Hull2D.Area(hull) * toMm * toMm;
                var length = Hull2D.Extent(flat) * toMm;
                // a sliver is a line: the hull is long but its area is tiny next to it
                if (area > 1e-6 && area > 0.001 * length * length) link.Area = area;
                else link.Length = length;
            }
            else
            {
                link.Normal = towards.IsTiny() ? Vector3d.ZAxis : towards;
                link.Normal.Unitize();
                if (pts.Count > 1) link.Length = Hull2D.Extent(pts.Select(p => (X: p.X, Y: p.Y)).ToList()) * toMm;
            }
            return link;
        }

        private static IEnumerable<KeyValuePair<Line, Color>> Wires(List<Body> bodies, List<Link> links)
        {
            foreach (var l in links)
            {
                var c = l.Kind == "gap" ? Color.Orange : l.Kind == "overlap" ? Color.Red : Color.LimeGreen;
                yield return new KeyValuePair<Line, Color>(new Line(bodies[l.A].Box.Center, l.At), c);
                yield return new KeyValuePair<Line, Color>(new Line(l.At, bodies[l.B].Box.Center), c);
            }
        }
    }
}
