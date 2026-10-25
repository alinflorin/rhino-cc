using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoCC.Core.Cli;

namespace RhinoCC.Cli
{
    /// <summary>The Rhino side of the fake CLI. Ported in spirit from the Tectopus / RhinoMCP functions, one command each.</summary>
    internal static class RhinoCommands
    {
        public static RhinoCli Build()
        {
            var cli = new RhinoCli
            {
                // Rhino's API is for its UI thread; the MCP server calls from a socket thread
                Invoker = f => UiThread.Run(f),
            };
            Document(cli);
            Layers(cli);
            Objects(cli);
            Editing(cli);
            Viewing(cli);
            Scripting(cli);
            return cli;
        }

        private static RhinoDoc Doc()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) throw new CliException("no document is open in Rhino");
            return doc;
        }

        // ---------------------------------------------------------------- document

        private static void Document(RhinoCli cli)
        {
            cli.Register(new CommandSpec("status", "Document", "status", "Rhino version, the open document, units, counts and the active view. Start here.", a =>
            {
                var doc = Doc();
                var sb = new StringBuilder();
                sb.AppendLine($"Rhino {RhinoApp.Version}");
                sb.AppendLine($"document: {(string.IsNullOrEmpty(doc.Name) ? "(untitled)" : doc.Name)}{(string.IsNullOrEmpty(doc.Path) ? "" : " at " + doc.Path)}{(doc.Modified ? " (unsaved changes)" : "")}");
                sb.AppendLine($"units: {doc.ModelUnitSystem}, absolute tolerance {Fmt.N(doc.ModelAbsoluteTolerance)}, angle tolerance {Fmt.N(doc.ModelAngleToleranceDegrees)}°");
                sb.AppendLine($"objects: {doc.Objects.Count()}, selected: {doc.Objects.GetSelectedObjects(false, false).Count()}");
                sb.AppendLine($"layers: {doc.Layers.Count(l => !l.IsDeleted)}, current: {doc.Layers.CurrentLayer.FullPath}");
                sb.Append($"active view: {doc.Views.ActiveView?.ActiveViewport.Name ?? "(none)"}");
                return CliResult.Ok(sb.ToString());
            }));

            cli.Register(new CommandSpec("save", "Document", "save", "Saves the document in place (it needs a file name already).", a =>
            {
                var doc = Doc();
                if (string.IsNullOrEmpty(doc.Path)) throw new CliException("the document has no file name yet; the user must save it once");
                RhinoApp.RunScript("-_Save _Enter", false);
                return CliResult.Ok("saved " + doc.Path);
            }));

            cli.Register(new CommandSpec("undo", "Document", "undo [N]", "Undoes the last N (default 1) Rhino operations.", a =>
            {
                var n = a.Positional.Count > 0 ? (int)a.PosNumber(0, "N") : 1;
                for (var i = 0; i < n; i++) RhinoApp.RunScript("_Undo", false);
                return CliResult.Ok($"undid {n}");
            }));

            cli.Register(new CommandSpec("redo", "Document", "redo [N]", "Redoes the last N (default 1) undone operations.", a =>
            {
                var n = a.Positional.Count > 0 ? (int)a.PosNumber(0, "N") : 1;
                for (var i = 0; i < n; i++) RhinoApp.RunScript("_Redo", false);
                return CliResult.Ok($"redid {n}");
            }));

            cli.Register(new CommandSpec("plugins", "Document", "plugins", "Lists the installed Rhino plugins (also the ones that add commands you can run with `cmd`).", a =>
            {
                var list = Rhino.PlugIns.PlugIn.GetInstalledPlugIns().Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                return CliResult.Ok(string.Join("\n", list));
            }));
        }

        // ---------------------------------------------------------------- layers

        private static void Layers(RhinoCli cli)
        {
            cli.Register(new CommandSpec("layers", "Layers", "layers", "Lists the layers with their colour, state and object count.", a =>
            {
                var doc = Doc();
                var lines = doc.Layers.Where(l => !l.IsDeleted).Select(l =>
                    $"{l.FullPath}{(l.Index == doc.Layers.CurrentLayerIndex ? " *" : "")}  color={l.Color.R},{l.Color.G},{l.Color.B}  {(l.IsVisible ? "visible" : "hidden")}{(l.IsLocked ? " locked" : "")}  objects={doc.Objects.FindByLayer(l)?.Length ?? 0}");
                return CliResult.Ok(string.Join("\n", lines) + "\n(* = current layer)");
            }));

            cli.Register(new CommandSpec("layer-new", "Layers", "layer-new NAME [--color R,G,B] [--parent LAYER]", "Creates a layer.", a =>
            {
                var doc = Doc();
                var layer = new Layer { Name = a.Pos(0, "NAME") };
                if (a.Has("color")) layer.Color = Fmt.Color(a.Opt("color"), "--color");
                if (a.Has("parent"))
                {
                    var parent = doc.Layers.FindByFullPath(a.Opt("parent"), RhinoMath.UnsetIntIndex);
                    if (parent == RhinoMath.UnsetIntIndex) throw new CliException($"no layer '{a.Opt("parent")}'");
                    layer.ParentLayerId = doc.Layers[parent].Id;
                }
                var index = doc.Layers.Add(layer);
                if (index < 0) throw new CliException("could not create the layer (does the name already exist?)");
                return CliResult.Ok("created layer " + doc.Layers[index].FullPath);
            }).Opt("color", "R,G,B", "Layer colour.").Opt("parent", "LAYER", "Full path of the parent layer."));

            cli.Register(new CommandSpec("layer-rm", "Layers", "layer-rm NAME", "Deletes a layer (and fails if it still holds objects).", a =>
            {
                var doc = Doc();
                var name = a.Pos(0, "NAME");
                var index = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
                if (index == RhinoMath.UnsetIntIndex) throw new CliException($"no layer '{name}'");
                return doc.Layers.Delete(index, true) ? CliResult.Ok("deleted layer " + name) : CliResult.Fail("could not delete the layer (it is current, or holds objects)");
            }));

            cli.Register(new CommandSpec("layer-current", "Layers", "layer-current [NAME]", "Shows the current layer, or makes NAME current.", a =>
            {
                var doc = Doc();
                if (a.Positional.Count == 0) return CliResult.Ok(doc.Layers.CurrentLayer.FullPath);
                var index = doc.Layers.FindByFullPath(a.Positional[0], RhinoMath.UnsetIntIndex);
                if (index == RhinoMath.UnsetIntIndex) throw new CliException($"no layer '{a.Positional[0]}'");
                doc.Layers.SetCurrentLayerIndex(index, true);
                return CliResult.Ok("current layer: " + doc.Layers[index].FullPath);
            }));
        }

        // ---------------------------------------------------------------- objects

        private static string Line(RhinoDoc doc, RhinoObject o)
        {
            var name = string.IsNullOrEmpty(o.Name) ? "" : $" \"{o.Name}\"";
            var layer = o.Attributes.LayerIndex >= 0 ? doc.Layers[o.Attributes.LayerIndex].FullPath : "?";
            return $"{Fmt.Short(o.Id)} {o.ObjectType}{name} layer={layer} bbox={Fmt.Box(o.Geometry.GetBoundingBox(true))}{(o.IsSelected(false) != 0 ? " selected" : "")}";
        }

        private static void Objects(RhinoCli cli)
        {
            cli.Register(new CommandSpec("objects", "Objects", "objects [--layer NAME] [--type TYPE] [--selected] [--limit N]", "Lists objects: short id, type, name, layer, bounding box.", a =>
            {
                var doc = Doc();
                IEnumerable<RhinoObject> all = doc.Objects;
                if (a.Has("selected")) all = doc.Objects.GetSelectedObjects(false, false);
                if (a.Has("layer"))
                {
                    var wanted = a.Opt("layer");
                    all = all.Where(o => o.Attributes.LayerIndex >= 0 && doc.Layers[o.Attributes.LayerIndex].FullPath.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                }
                if (a.Has("type"))
                    all = all.Where(o => o.ObjectType.ToString().Equals(a.Opt("type"), StringComparison.OrdinalIgnoreCase));
                var list = all.ToList();
                var limit = (int)(a.OptNumber("limit") ?? 100);
                var sb = new StringBuilder();
                foreach (var o in list.Take(limit)) sb.AppendLine(Line(doc, o));
                sb.Append(list.Count > limit ? $"({list.Count} objects, showing {limit}; use --limit)" : $"({list.Count} objects)");
                return CliResult.Ok(sb.ToString());
            }).Opt("layer", "NAME", "Only this layer (full path).").Opt("type", "TYPE", "Only this object type: Curve, Brep, Extrusion, Point, Mesh, Surface, ...")
              .Flag("selected", "Only the selected objects.").Opt("limit", "N", "Show at most N objects (default 100)."));

            cli.Register(new CommandSpec("info", "Objects", "info ID...", "Details of objects: full id, geometry, size, volume/area/length where they exist.", a =>
            {
                var doc = Doc();
                var sb = new StringBuilder();
                foreach (var id in Fmt.Ids(doc, a.Positional))
                {
                    var o = doc.Objects.FindId(id);
                    sb.AppendLine($"{id} {Line(doc, o).Substring(9)}");
                    var g = o.Geometry;
                    var box = g.GetBoundingBox(true);
                    if (box.IsValid) sb.AppendLine($"  size: {Fmt.N(box.Diagonal.X)} x {Fmt.N(box.Diagonal.Y)} x {Fmt.N(box.Diagonal.Z)}, center {Fmt.P(box.Center)}");
                    if (g is Curve c)
                        sb.AppendLine($"  curve: length {Fmt.N(c.GetLength())}, {(c.IsClosed ? "closed" : "open")}, {(c.IsPlanar() ? "planar" : "not planar")}, degree {c.Degree}");
                    else if (g is Brep b)
                    {
                        sb.AppendLine($"  brep: {b.Faces.Count} faces, {b.Edges.Count} edges, {(b.IsSolid ? "solid" : "open")}");
                        var area = AreaMassProperties.Compute(b);
                        if (area != null) sb.AppendLine($"  area: {Fmt.N(area.Area)}");
                        if (b.IsSolid)
                        {
                            var vol = VolumeMassProperties.Compute(b);
                            if (vol != null) sb.AppendLine($"  volume: {Fmt.N(vol.Volume)}, centroid {Fmt.P(vol.Centroid)}");
                        }
                    }
                    else if (g is Extrusion e)
                    {
                        sb.AppendLine($"  extrusion: {(e.IsSolid ? "solid" : "open")}");
                        var vol = e.IsSolid ? VolumeMassProperties.Compute(e) : null;
                        if (vol != null) sb.AppendLine($"  volume: {Fmt.N(vol.Volume)}, centroid {Fmt.P(vol.Centroid)}");
                    }
                    else if (g is Mesh m)
                        sb.AppendLine($"  mesh: {m.Vertices.Count} vertices, {m.Faces.Count} faces, {(m.IsClosed ? "closed" : "open")}");
                    else if (g is Rhino.Geometry.Point p)
                        sb.AppendLine($"  point: {Fmt.P(p.Location)}");
                    if (o.Attributes.ColorSource == ObjectColorSource.ColorFromObject)
                        sb.AppendLine($"  color: {o.Attributes.ObjectColor.R},{o.Attributes.ObjectColor.G},{o.Attributes.ObjectColor.B}");
                }
                return CliResult.Ok(sb.ToString().TrimEnd());
            }));

            cli.Register(new CommandSpec("add", "Objects", "add KIND ARGS... [--at X,Y,Z] [--name N] [--layer L] [--color R,G,B]", "Adds geometry and prints its id. KIND: point, line, polyline, curve, circle, arc, ellipse, box, sphere, cylinder, cone. Solids are centred on --at (default the origin).", a =>
            {
                var doc = Doc();
                var kind = a.Pos(0, "KIND").ToLowerInvariant();
                var at = a.Has("at") ? Fmt.Point(a.Opt("at"), "--at") : Point3d.Origin;
                var axis = (a.Opt("axis") ?? "z").ToLowerInvariant();
                var dir = axis == "x" ? Vector3d.XAxis : axis == "y" ? Vector3d.YAxis : axis == "z" ? Vector3d.ZAxis : throw new CliException("--axis is x, y or z");
                var attrs = Attributes(doc, a);
                Guid id;
                switch (kind)
                {
                    case "point":
                        id = doc.Objects.AddPoint(Fmt.Point(a.Pos(1, "POINT"), "POINT"), attrs);
                        break;
                    case "line":
                        id = doc.Objects.AddLine(Fmt.Point(a.Pos(1, "FROM"), "FROM"), Fmt.Point(a.Pos(2, "TO"), "TO"), attrs);
                        break;
                    case "polyline":
                        var pts = a.Positional.Skip(1).Select(t => Fmt.Point(t, "POINT")).ToList();
                        if (pts.Count < 2) throw new CliException("polyline needs at least 2 points");
                        if (a.Has("close")) pts.Add(pts[0]);
                        id = doc.Objects.AddPolyline(pts, attrs);
                        break;
                    case "circle":
                        id = doc.Objects.AddCircle(new Circle(new Plane(at, dir), a.PosNumber(1, "RADIUS")), attrs);
                        break;
                    case "arc":
                        id = doc.Objects.AddArc(new Arc(new Plane(at, dir), a.PosNumber(1, "RADIUS"), RhinoMath.ToRadians(a.PosNumber(2, "ANGLE(deg)"))), attrs);
                        break;
                    case "ellipse":
                        id = doc.Objects.AddEllipse(new Ellipse(new Plane(at, dir), a.PosNumber(1, "RADIUS-X"), a.PosNumber(2, "RADIUS-Y")), attrs);
                        break;
                    case "curve":
                        {
                            var through = a.Positional.Skip(1).Select(t => Fmt.Point(t, "POINT")).ToList();
                            var curve = Curve.CreateControlPointCurve(through, (int)(a.OptNumber("degree") ?? 3)) ?? throw new CliException("cannot make a curve from those points (it needs more points than the degree)");
                            id = doc.Objects.AddCurve(curve, attrs);
                            break;
                        }
                    case "box":
                        {
                            var w = a.PosNumber(1, "WIDTH(x)"); var l = a.PosNumber(2, "LENGTH(y)"); var h = a.PosNumber(3, "HEIGHT(z)");
                            var box = new Box(new Plane(at, Vector3d.XAxis, Vector3d.YAxis), new Interval(-w / 2, w / 2), new Interval(-l / 2, l / 2), new Interval(-h / 2, h / 2));
                            id = doc.Objects.AddBrep(box.ToBrep(), attrs);
                            break;
                        }
                    case "sphere":
                        id = doc.Objects.AddBrep(new Sphere(at, a.PosNumber(1, "RADIUS")).ToBrep(), attrs);
                        break;
                    case "cylinder":
                    case "cone":
                        {
                            var r = a.PosNumber(1, "RADIUS"); var h = a.PosNumber(2, "HEIGHT");
                            var basePlane = new Plane(at - dir * (h / 2), dir);
                            var cap = a.Has("cap");
                            var brep = kind == "cone"
                                ? Brep.CreateFromCone(new Cone(basePlane, h, r), cap)
                                : new Cylinder(new Circle(basePlane, r), h).ToBrep(cap, cap);
                            id = doc.Objects.AddBrep(brep, attrs);
                            break;
                        }
                    default:
                        throw new CliException($"unknown kind '{kind}' (point, line, polyline, curve, circle, arc, ellipse, box, sphere, cylinder, cone)");
                }
                if (id == Guid.Empty) throw new CliException("Rhino did not add the object (check the sizes)");
                doc.Views.Redraw();
                return CliResult.Ok($"added {kind} {id}");
            }).Opt("at", "X,Y,Z", "Centre / origin.").Opt("axis", "x|y|z", "Axis of circle, cylinder and cone (default z).").Flag("cap", "Cap the ends of a cylinder or cone.")
              .Flag("close", "Close a polyline.").Opt("degree", "N", "Degree of a curve through control points (default 3).").Opt("name", "N", "Object name.").Opt("layer", "L", "Layer (created if missing).").Opt("color", "R,G,B", "Object colour.")
              .Example("add box 20 10 5 --at 0,0,2.5 --name base")
              .Example("add polyline 0,0 10,0 10,10 --close")
              .Example("add cylinder 3 12 --cap --axis x"));
        }

        private static ObjectAttributes Attributes(RhinoDoc doc, CliArgs a)
        {
            var attrs = new ObjectAttributes();
            ApplyAttributes(doc, attrs, a);
            return attrs;
        }

        private static void ApplyAttributes(RhinoDoc doc, ObjectAttributes attrs, CliArgs a)
        {
            if (a.Has("name")) attrs.Name = a.Opt("name");
            if (a.Has("layer"))
            {
                var name = a.Opt("layer");
                var index = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
                if (index == RhinoMath.UnsetIntIndex) index = doc.Layers.Add(name, System.Drawing.Color.Black);
                if (index < 0) throw new CliException($"could not use layer '{name}'");
                attrs.LayerIndex = index;
            }
            if (a.Has("color"))
            {
                attrs.ObjectColor = Fmt.Color(a.Opt("color"), "--color");
                attrs.ColorSource = ObjectColorSource.ColorFromObject;
            }
        }

        // ---------------------------------------------------------------- editing

        /// <summary>Transforms (or copies) an object.</summary>
        private static Guid Xf(RhinoDoc doc, Guid id, Transform x, bool copy)
        {
            return doc.Objects.Transform(id, x, !copy);
        }

        private static CommandSpec Edit(string name, string usage, string summary, Func<CliArgs, RhinoDoc, List<Guid>, CliResult> run) =>
            new CommandSpec(name, "Editing", usage, summary, a => run(a, Doc(), Fmt.Ids(Doc(), a.Positional)));

        private static void Editing(RhinoCli cli)
        {
            cli.Register(Edit("move", "move ID... --by X,Y,Z", "Moves objects by a vector.", (a, doc, ids) =>
            {
                var x = Transform.Translation(Fmt.Vector(a.Opt("by") ?? throw new CliException("--by X,Y,Z is required"), "--by"));
                foreach (var id in ids) Xf(doc, id, x, false);
                doc.Views.Redraw();
                return CliResult.Ok($"moved {ids.Count} object(s)");
            }).Opt("by", "X,Y,Z", "The offset."));

            cli.Register(Edit("rotate", "rotate ID... --angle DEG [--axis x|y|z] [--center X,Y,Z]", "Rotates objects about an axis through a centre (default z, through the origin).", (a, doc, ids) =>
            {
                var angle = a.OptNumber("angle") ?? throw new CliException("--angle DEG is required");
                var axis = (a.Opt("axis") ?? "z").ToLowerInvariant();
                var dir = axis == "x" ? Vector3d.XAxis : axis == "y" ? Vector3d.YAxis : axis == "z" ? Vector3d.ZAxis : throw new CliException("--axis is x, y or z");
                var center = a.Has("center") ? Fmt.Point(a.Opt("center"), "--center") : Point3d.Origin;
                var x = Transform.Rotation(RhinoMath.ToRadians(angle), dir, center);
                foreach (var id in ids) Xf(doc, id, x, false);
                doc.Views.Redraw();
                return CliResult.Ok($"rotated {ids.Count} object(s) by {Fmt.N(angle)}° about {axis}");
            }).Opt("angle", "DEG", "Angle in degrees.").Opt("axis", "x|y|z", "Axis (default z).").Opt("center", "X,Y,Z", "A point on the axis (default the origin)."));

            cli.Register(Edit("scale", "scale ID... --factor F [--center X,Y,Z]", "Scales objects uniformly about a centre (default the origin).", (a, doc, ids) =>
            {
                var f = a.OptNumber("factor") ?? throw new CliException("--factor F is required");
                var center = a.Has("center") ? Fmt.Point(a.Opt("center"), "--center") : Point3d.Origin;
                var x = Transform.Scale(center, f);
                foreach (var id in ids) Xf(doc, id, x, false);
                doc.Views.Redraw();
                return CliResult.Ok($"scaled {ids.Count} object(s) by {Fmt.N(f)}");
            }).Opt("factor", "F", "Scale factor.").Opt("center", "X,Y,Z", "Centre of scaling."));

            cli.Register(Edit("copy", "copy ID... [--by X,Y,Z]", "Copies objects, optionally offset; prints the new ids.", (a, doc, ids) =>
            {
                var x = a.Has("by") ? Transform.Translation(Fmt.Vector(a.Opt("by"), "--by")) : Transform.Identity;
                var made = ids.Select(id => Xf(doc, id, x, true)).Where(g => g != Guid.Empty).ToList();
                doc.Views.Redraw();
                return CliResult.Ok("copied to " + string.Join(" ", made));
            }).Opt("by", "X,Y,Z", "Offset of the copy."));

            cli.Register(Edit("delete", "delete ID...", "Deletes objects.", (a, doc, ids) =>
            {
                var n = ids.Count(id => doc.Objects.Delete(id, true));
                doc.Views.Redraw();
                return CliResult.Ok($"deleted {n} object(s)");
            }));

            cli.Register(Edit("set", "set ID... [--name N] [--layer L] [--color R,G,B]", "Changes name, layer or colour of objects.", (a, doc, ids) =>
            {
                foreach (var id in ids)
                {
                    var o = doc.Objects.FindId(id);
                    var attrs = o.Attributes.Duplicate();
                    ApplyAttributes(doc, attrs, a);
                    doc.Objects.ModifyAttributes(o, attrs, true);
                }
                doc.Views.Redraw();
                return CliResult.Ok($"updated {ids.Count} object(s)");
            }).Opt("name", "N", "New name.").Opt("layer", "L", "Layer (created if missing).").Opt("color", "R,G,B", "Object colour."));

            cli.Register(new CommandSpec("select", "Editing", "select ID... | --all | --layer NAME | --name NAME | --type TYPE | --none", "Changes the selection (what the user sees highlighted). Filters add up.", a =>
            {
                var doc = Doc();
                doc.Objects.UnselectAll();
                var n = 0;
                if (a.Has("none")) { }
                else if (a.Has("name") || a.Has("type"))
                {
                    var hits = doc.Objects.Where(o =>
                        (a.Has("name") && string.Equals(o.Name, a.Opt("name"), StringComparison.OrdinalIgnoreCase)) ||
                        (a.Has("type") && o.ObjectType.ToString().Equals(a.Opt("type"), StringComparison.OrdinalIgnoreCase))).Select(o => o.Id).ToList();
                    n = doc.Objects.Select(hits);
                }
                else if (a.Has("all")) n = doc.Objects.Select(doc.Objects.Select(o => o.Id).ToList()) ;
                else if (a.Has("layer"))
                {
                    var layer = a.Opt("layer");
                    n = doc.Objects.Select(doc.Objects.Where(o => o.Attributes.LayerIndex >= 0 && doc.Layers[o.Attributes.LayerIndex].FullPath.Equals(layer, StringComparison.OrdinalIgnoreCase)).Select(o => o.Id).ToList());
                }
                else n = doc.Objects.Select(Fmt.Ids(doc, a.Positional));
                doc.Views.Redraw();
                return CliResult.Ok($"{n} object(s) selected");
            }).Flag("all", "Select everything.").Opt("layer", "NAME", "Select a layer's objects.").Opt("name", "NAME", "Select objects with this exact name.")
              .Opt("type", "TYPE", "Select by object type: Brep, Extrusion, Curve, Point, Mesh, ...").Flag("none", "Clear the selection."));
        }

        // ---------------------------------------------------------------- viewing

        private static void Viewing(RhinoCli cli)
        {
            cli.Register(new CommandSpec("shot", "View", "shot [--view NAME] [--display MODE] [--size WxH] [--zoom ID...|selected]", "Takes a picture of a viewport and returns it. Restores the viewport afterwards.", a =>
            {
                var doc = Doc();
                var view = doc.Views.ActiveView ?? throw new CliException("there is no active view");
                var vp = view.ActiveViewport;
                var size = (a.Opt("size") ?? "1000x700").Split('x');
                if (size.Length != 2 || !int.TryParse(size[0], out var w) || !int.TryParse(size[1], out var h) || w < 64 || h < 64 || w > 3840 || h > 3840)
                    throw new CliException("--size is WxH, between 64 and 3840");

                var previousMode = vp.DisplayMode;
                vp.PushViewProjection();
                try
                {
                    if (a.Has("view"))
                    {
                        var proj = ParseProjection(a.Opt("view"));
                        vp.SetProjection(proj, a.Opt("view"), false);
                    }
                    if (a.Has("display"))
                    {
                        var mode = Rhino.Display.DisplayModeDescription.FindByName(a.Opt("display"));
                        if (mode == null)
                            throw new CliException($"no display mode '{a.Opt("display")}'. Available: " + string.Join(", ", Rhino.Display.DisplayModeDescription.GetDisplayModes().Select(m => m.EnglishName)));
                        vp.DisplayMode = mode;
                    }
                    var targets = a.Has("zoom") ? (a.Opt("zoom") == "selected" ? doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList() : Fmt.Ids(doc, new[] { a.Opt("zoom") })) : null;
                    if (targets != null && targets.Count > 0)
                    {
                        var box = BoundingBox.Empty;
                        foreach (var id in targets) box.Union(doc.Objects.FindId(id).Geometry.GetBoundingBox(true));
                        vp.ZoomBoundingBox(box);
                    }
                    else if (a.Has("view")) vp.ZoomExtents();
                    view.Redraw();

                    var capture = new Rhino.Display.ViewCapture { Width = w, Height = h, ScaleScreenItems = false, DrawAxes = false, DrawGrid = false, DrawGridAxes = false, TransparentBackground = false };
                    using (var bmp = capture.CaptureToBitmap(view))
                    {
                        if (bmp == null) throw new CliException("Rhino could not capture the view");
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            var r = CliResult.Ok($"{vp.Name} view, {vp.DisplayMode.EnglishName}, {w}x{h}");
                            r.Images.Add(new CliImage { Mime = "image/png", Base64 = Convert.ToBase64String(ms.ToArray()) });
                            return r;
                        }
                    }
                }
                finally
                {
                    vp.DisplayMode = previousMode;
                    vp.PopViewProjection();
                    view.Redraw();
                }
            }).Opt("view", "NAME", "top, bottom, front, back, left, right, perspective or iso (default: the active view as it is).")
              .Opt("display", "MODE", "A display mode, e.g. Shaded, Wireframe, Rendered, Ghosted, Arctic.")
              .Opt("size", "WxH", "Pixels, default 1000x700.").Opt("zoom", "ID|selected", "Frame this object (or the selection) first.")
              .Example("shot --view top --display Wireframe")
              .Example("shot --view iso --zoom 3fa85f64"));

            cli.Register(new CommandSpec("views", "View", "views", "Lists the viewports and their display modes.", a =>
            {
                var doc = Doc();
                return CliResult.Ok(string.Join("\n", doc.Views.Select(v => $"{v.ActiveViewport.Name}{(v == doc.Views.ActiveView ? " *" : "")}  {v.ActiveViewport.DisplayMode.EnglishName}")) + "\n(* = active)");
            }));
        }

        private static Rhino.Display.DefinedViewportProjection ParseProjection(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "top": return Rhino.Display.DefinedViewportProjection.Top;
                case "bottom": return Rhino.Display.DefinedViewportProjection.Bottom;
                case "front": return Rhino.Display.DefinedViewportProjection.Front;
                case "back": return Rhino.Display.DefinedViewportProjection.Back;
                case "left": return Rhino.Display.DefinedViewportProjection.Left;
                case "right": return Rhino.Display.DefinedViewportProjection.Right;
                case "perspective": return Rhino.Display.DefinedViewportProjection.Perspective;
                case "iso": return Rhino.Display.DefinedViewportProjection.Perspective;
                default: throw new CliException($"unknown view '{name}' (top, bottom, front, back, left, right, perspective, iso)");
            }
        }

        // ---------------------------------------------------------------- scripting

        private static string History() => RhinoApp.CommandHistoryWindowText ?? "";

        private static string NewHistory(string before)
        {
            var after = History();
            return after.StartsWith(before, StringComparison.Ordinal) ? after.Substring(before.Length).Trim() : after.Trim();
        }

        private static void Scripting(RhinoCli cli)
        {
            cli.Register(new CommandSpec("cmd", "Scripting", "cmd \"RHINO COMMAND LINE\"", "Runs a Rhino command exactly as typed, e.g. \"-_Fillet 2 _Enter\". Prefer the hyphenated, underscore-prefixed forms (no dialogs). Prints what Rhino said.", a =>
            {
                var doc = Doc();
                var text = string.Join(" ", a.Positional);
                if (text.Length == 0) throw new CliException("missing the Rhino command");
                var before = History();
                var ok = RhinoApp.RunScript(text, false);
                doc.Views.Redraw();
                var said = NewHistory(before);
                return ok ? CliResult.Ok(said.Length == 0 ? "(done)" : said) : CliResult.Fail("Rhino could not run it" + (said.Length == 0 ? "" : ":\n" + said));
            }).Example("cmd \"-_BooleanUnion _SelAll _Enter\""));

            cli.Register(new CommandSpec("script", "Scripting", "script <<EOF ... EOF", "Runs a Python script (rhinoscriptsyntax or RhinoCommon) given as a here-document. Prints what Rhino said.", a =>
            {
                var doc = Doc();
                var code = a.Pos(0, "the script (use a here-document)");
                var file = Path.Combine(Path.GetTempPath(), "rhinocc-script-" + Guid.NewGuid().ToString("N") + ".py");
                File.WriteAllText(file, code);
                try
                {
                    var before = History();
                    var ok = RhinoApp.RunScript($"-_RunPythonScript \"{file}\"", false);
                    doc.Views.Redraw();
                    var said = NewHistory(before);
                    return ok ? CliResult.Ok(said.Length == 0 ? "(done)" : said) : CliResult.Fail("the script did not run" + (said.Length == 0 ? "" : ":\n" + said));
                }
                finally
                {
                    try { File.Delete(file); } catch { /* temp file */ }
                }
            }).Example("script <<EOF\nimport rhinoscriptsyntax as rs\nprint(len(rs.AllObjects()))\nEOF"));

            cli.Register(new CommandSpec("log", "Scripting", "log [N]", "The last N (default 30) lines of Rhino's command history.", a =>
            {
                var n = a.Positional.Count > 0 ? (int)a.PosNumber(0, "N") : 30;
                var lines = History().Replace("\r\n", "\n").Split('\n');
                return CliResult.Ok(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n))));
            }));
        }
    }
}
