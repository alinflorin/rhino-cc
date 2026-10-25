using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Geometry;

namespace RhinoCC.Cli
{
    /// <summary>`pose`, `pose-rebase`, `pose-reset`, `obb`: each object's oriented box and putting objects back to their own axes.</summary>
    internal static class PoseCommands
    {
        public static void Register(RhinoCli cli)
        {
            cli.Register(new CommandSpec("pose", "Pose", "pose ID... | --selected | --name NAME",
                "Each object's oriented box: centre, the x/y/z axes of its tightest box and the side lengths along them (in model units). Found from the geometry, so a leaning part reports its own axes, not the world's; `pose-rebase` pins them.", a =>
            {
                var doc = RhinoCommands.Doc();
                var sb = new StringBuilder();
                foreach (var o in Objects(doc, a)) sb.AppendLine(Line(o, Poses.Get(o)));
                return CliResult.Ok(sb.ToString().TrimEnd());
            }).Flag("selected", "The current selection.").Opt("name", "NAME", "Objects with this name."));

            cli.Register(new CommandSpec("pose-rebase", "Pose", "pose-rebase ID... | --selected | --name NAME [--z +z|-z|+x|-x|+y|-y] [--x +x|-x|+y|-y|+z|-z]",
                "Pins the object's current placement as its own frame without moving it: axes snapped to the nearest signed world axes (--z / --x say which way 'up' and 'along' are), centre at the box centre. Later `move` / `rotate` / `scale` / `copy` carry the frame along; `pose-reset` brings the object back to the world axes.", a =>
            {
                var doc = RhinoCommands.Doc();
                var sb = new StringBuilder();
                foreach (var o in Objects(doc, a))
                {
                    var p = Poses.Get(o);
                    double[][] f;
                    try { f = Rotation.SnapFrame(V(p.Frame.XAxis), V(p.Frame.YAxis), V(p.Frame.ZAxis), a.Opt("z"), a.Opt("x")); }
                    catch (ArgumentException e) { throw new CliException(e.Message); }
                    var pinned = Poses.Measure(o, new Plane(p.Frame.Origin, new Vector3d(f[0][0], f[0][1], f[0][2]), new Vector3d(f[1][0], f[1][1], f[1][2])));
                    pinned.Pinned = true;
                    Poses.Save(doc, o.Id, pinned);
                    sb.AppendLine("pinned " + Line(doc.Objects.FindId(o.Id), pinned));
                }
                return CliResult.Ok(sb.ToString().TrimEnd());
            }).Flag("selected", "The current selection.").Opt("name", "NAME", "Objects with this name.")
              .Opt("z", "+z|-z|...", "Which world direction the box's z axis must point to.").Opt("x", "+x|-x|...", "Which world direction the box's x axis must point to."));

            cli.Register(new CommandSpec("pose-reset", "Pose", "pose-reset ID... | --selected | --name NAME [--to X,Y,Z] [--keep-position] [--keep-rotation]",
                "Turns each object so its box axes line up with the world axes and puts its box centre at --to (default the origin). --keep-position only turns it where it is; --keep-rotation only moves it.", a =>
            {
                var doc = RhinoCommands.Doc();
                if (a.Has("keep-position") && a.Has("keep-rotation")) throw new CliException("--keep-position and --keep-rotation together would do nothing");
                var to = a.Has("to") ? Fmt.Point(a.Opt("to"), "--to") : Point3d.Origin;
                var sb = new StringBuilder();
                foreach (var o in Objects(doc, a))
                {
                    var p = Poses.Get(o);
                    var target = a.Has("keep-position") ? p.Frame.Origin : to;
                    var x = a.Has("keep-rotation")
                        ? Transform.Translation(target - p.Frame.Origin)
                        : Transform.PlaneToPlane(p.Frame, new Plane(target, Vector3d.XAxis, Vector3d.YAxis));
                    var id = Poses.Transform(doc, o.Id, x, false);
                    sb.AppendLine("reset " + Line(doc.Objects.FindId(id), Poses.Get(doc.Objects.FindId(id))));
                }
                doc.Views.Redraw();
                return CliResult.Ok(sb.ToString().TrimEnd());
            }).Flag("selected", "The current selection.").Opt("name", "NAME", "Objects with this name.")
              .Opt("to", "X,Y,Z", "Where the box centre goes (default the origin).")
              .Flag("keep-position", "Turn only; the centre stays.").Flag("keep-rotation", "Move only; the orientation stays."));

            cli.Register(new CommandSpec("obb", "Pose", "obb [ID... | --selected | --layer NAME | --name NAME] | --off",
                "Draws each object's oriented box over the model (gold box, red/green/blue x/y/z axes), so `shot` shows it. A snapshot of the poses now: run it again after moving things. Without targets: every visible solid, surface, curve and mesh.", a =>
            {
                if (a.Has("off")) { Overlay.Boxes.Clear(); RhinoCommands.Doc().Views.Redraw(); return CliResult.Ok("boxes hidden"); }
                var doc = RhinoCommands.Doc();
                var all = a.Positional.Count > 0 || a.Has("selected") || a.Has("name") ? Objects(doc, a) : Visible(doc, a);
                if (all.Count == 0) throw new CliException("no objects to draw");
                var lines = new List<System.Collections.Generic.KeyValuePair<Line, System.Drawing.Color>>();
                foreach (var o in all.Take(200)) lines.AddRange(Poses.Get(o).Wire());
                Overlay.Boxes.Set(lines);
                doc.Views.Redraw();
                return CliResult.Ok($"drawing the boxes of {Math.Min(all.Count, 200)} object(s); `obb --off` removes them");
            }).Flag("selected", "The current selection.").Opt("name", "NAME", "Objects with this name.").Opt("layer", "NAME", "Only this layer.").Flag("off", "Remove the drawing."));
        }

        private static List<RhinoObject> Objects(RhinoDoc doc, CliArgs a)
        {
            IEnumerable<Guid> ids;
            if (a.Has("selected"))
            {
                ids = doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList();
                if (!ids.Any()) throw new CliException("nothing is selected");
            }
            else if (a.Has("name"))
            {
                ids = doc.Objects.Where(o => string.Equals(o.Name, a.Opt("name"), StringComparison.OrdinalIgnoreCase)).Select(o => o.Id).ToList();
                if (!ids.Any()) throw new CliException($"no object named '{a.Opt("name")}'");
            }
            else ids = Fmt.Ids(doc, a.Positional);
            return ids.Select(doc.Objects.FindId).ToList();
        }

        private static List<RhinoObject> Visible(RhinoDoc doc, CliArgs a) =>
            doc.Objects.Where(o => !o.IsHidden && !(o.Geometry is Rhino.Geometry.Light) && !(o.Geometry is Rhino.Geometry.Point)
                && (!a.Has("layer") || (o.Attributes.LayerIndex >= 0 && doc.Layers[o.Attributes.LayerIndex].FullPath.Equals(a.Opt("layer"), StringComparison.OrdinalIgnoreCase)))).ToList();

        private static double[] V(Vector3d v) => new[] { v.X, v.Y, v.Z };

        private static string Line(RhinoObject o, Pose p) =>
            $"{Fmt.Short(o.Id)}{(string.IsNullOrEmpty(o.Name) ? "" : $" \"{o.Name}\"")} {o.ObjectType}: centre {Fmt.P(p.Frame.Origin)}  x {Fmt.P(p.Frame.XAxis)}  y {Fmt.P(p.Frame.YAxis)}  z {Fmt.P(p.Frame.ZAxis)}  size {Fmt.N(p.Size.X)} x {Fmt.N(p.Size.Y)} x {Fmt.N(p.Size.Z)}{(p.Pinned ? "  (pinned)" : "")}";
    }
}
