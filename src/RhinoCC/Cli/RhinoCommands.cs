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
            Materials(cli);
            Viewing(cli);
            AnalyzeCommand.Register(cli);
            Scripting(cli);
            return cli;
        }

        internal static RhinoDoc Doc()
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

        /// <summary>Rhino's version and installed plugins, for the agent's instructions (read fresh for every turn).</summary>
        public static string EnvironmentInfo(bool onUiThread)
        {
            try
            {
                Func<CliResult> read = () =>
                {
                    var names = Rhino.PlugIns.PlugIn.GetInstalledPlugIns().Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    var list = string.Join(", ", names);
                    if (list.Length > 600) list = list.Substring(0, list.LastIndexOf(',', 600)) + ", … (`plugins` lists all)";
                    return CliResult.Ok($"## This Rhino\n\nRhino {RhinoApp.Version}\nInstalled plugins ({names.Count}): {list}");
                };
                // from the UI thread, going through the invoker would wait on itself
                return (onUiThread ? read() : UiThread.Run(read)).Text;
            }
            catch (Exception)
            {
                return null; // the instructions are still useful without it
            }
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

            cli.Register(new CommandSpec("layer-set", "Layers", "layer-set NAME [--rename NEW] [--color R,G,B] [--visible on|off] [--locked on|off]", "Renames a layer, recolours it, hides/shows it or locks/unlocks it. Objects move between layers with `set --layer`.", a =>
            {
                var doc = Doc();
                var name = a.Pos(0, "NAME");
                var index = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
                if (index == RhinoMath.UnsetIntIndex) throw new CliException($"no layer '{name}'");
                if (!a.Has("rename") && !a.Has("color") && !a.Has("visible") && !a.Has("locked")) throw new CliException("nothing to change; give at least one option");
                var layer = doc.Layers[index];
                if (a.Has("rename")) layer.Name = a.Opt("rename");
                if (a.Has("color")) layer.Color = Fmt.Color(a.Opt("color"), "--color");
                if (a.Has("visible")) layer.IsVisible = Fmt.OnOff(a.Opt("visible"), "--visible");
                if (a.Has("locked")) layer.IsLocked = Fmt.OnOff(a.Opt("locked"), "--locked");
                if (!doc.Layers.Modify(layer, index, true)) throw new CliException("Rhino refused the change (a layer being current cannot be hidden, and names must be unique)");
                doc.Views.Redraw();
                return CliResult.Ok("updated layer " + doc.Layers[index].FullPath);
            }).Opt("rename", "NEW", "New name (not a path).").Opt("color", "R,G,B", "Layer colour.").Opt("visible", "on|off", "Show or hide.").Opt("locked", "on|off", "Lock or unlock."));

            cli.Register(new CommandSpec("layer-state", "Layers", "layer-state [save NAME | restore NAME | rm NAME]", "Saved sets of layer visibility and locks (kept in the file), e.g. to hide construction layers for a picture and bring them back. Without arguments, lists them.", a =>
            {
                var doc = Doc();
                const string prefix = "RhinoCC.layerstate.";
                if (a.Positional.Count == 0)
                {
                    var names = new List<string>();
                    for (var i = 0; i < doc.Strings.Count; i++)
                    {
                        var key = doc.Strings.GetKey(i);
                        if (key != null && key.StartsWith(prefix, StringComparison.Ordinal)) names.Add(key.Substring(prefix.Length));
                    }
                    return CliResult.Ok(names.Count == 0 ? "(no saved layer states)" : string.Join("\n", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
                }
                var action = a.Positional[0].ToLowerInvariant();
                var state = a.Pos(1, "NAME");
                var stateKey = prefix + state;
                switch (action)
                {
                    case "save":
                        doc.Strings.SetString(stateKey, string.Join("\n", doc.Layers.Where(l => !l.IsDeleted).Select(l => $"{(l.IsVisible ? 1 : 0)}{(l.IsLocked ? 1 : 0)}{l.FullPath}")));
                        return CliResult.Ok($"saved layer state '{state}'");
                    case "restore":
                        {
                            var text = doc.Strings.GetValue(stateKey) ?? throw new CliException($"no layer state '{state}'");
                            var n = 0;
                            foreach (var line in text.Split('\n'))
                            {
                                if (line.Length < 3) continue;
                                var index = doc.Layers.FindByFullPath(line.Substring(2), RhinoMath.UnsetIntIndex);
                                if (index == RhinoMath.UnsetIntIndex) continue; // the layer was deleted since
                                var layer = doc.Layers[index];
                                layer.IsVisible = line[0] == '1' || index == doc.Layers.CurrentLayerIndex;
                                layer.IsLocked = line[1] == '1';
                                if (doc.Layers.Modify(layer, index, true)) n++;
                            }
                            doc.Views.Redraw();
                            return CliResult.Ok($"restored layer state '{state}' ({n} layers)");
                        }
                    case "rm":
                        if (doc.Strings.GetValue(stateKey) == null) throw new CliException($"no layer state '{state}'");
                        doc.Strings.Delete(stateKey);
                        return CliResult.Ok($"removed layer state '{state}'");
                    default:
                        throw new CliException($"unknown action '{action}' (save, restore, rm)");
                }
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
            cli.Register(new CommandSpec("objects", "Objects", "objects [--layer NAME] [--type TYPE] [--name TEXT] [--selected] [--bbox X0,Y0,Z0,X1,Y1,Z1 [--inside]] [--summary] [--limit N] [--offset N]", "Lists objects: short id, type, name, layer, bounding box. --summary prints counts per type and layer instead.", a =>
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
                if (a.Has("name"))
                    all = all.Where(o => !string.IsNullOrEmpty(o.Name) && o.Name.IndexOf(a.Opt("name"), StringComparison.OrdinalIgnoreCase) >= 0);
                if (a.Has("bbox"))
                {
                    var region = Fmt.Region(a.Opt("bbox"), "--bbox");
                    var inside = a.Has("inside");
                    all = all.Where(o =>
                    {
                        var b = o.Geometry.GetBoundingBox(true);
                        if (!b.IsValid) return false;
                        return inside ? region.Contains(b.Min) && region.Contains(b.Max) : BoundingBox.Intersection(region, b).IsValid;
                    });
                }
                var list = all.ToList();
                if (a.Has("summary"))
                {
                    var box = BoundingBox.Empty;
                    foreach (var o in list) box.Union(o.Geometry.GetBoundingBox(true));
                    var sum = new StringBuilder();
                    sum.AppendLine($"{list.Count} objects, overall bbox {Fmt.Box(box)}");
                    sum.AppendLine("by type: " + string.Join(", ", list.GroupBy(o => o.ObjectType.ToString()).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
                    sum.Append("by layer: " + string.Join(", ", list.GroupBy(o => o.Attributes.LayerIndex >= 0 ? doc.Layers[o.Attributes.LayerIndex].FullPath : "?").OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
                    return CliResult.Ok(sum.ToString());
                }
                var limit = (int)(a.OptNumber("limit") ?? 100);
                var offset = (int)(a.OptNumber("offset") ?? 0);
                var sb = new StringBuilder();
                foreach (var o in list.Skip(offset).Take(limit)) sb.AppendLine(Line(doc, o));
                sb.Append(list.Count > offset + limit ? $"({list.Count} objects, showing {offset + 1}-{offset + limit}; use --offset {offset + limit} for the next page)" : $"({list.Count} objects)");
                return CliResult.Ok(sb.ToString());
            }).Opt("layer", "NAME", "Only this layer (full path).").Opt("type", "TYPE", "Only this object type: Curve, Brep, Extrusion, Point, Mesh, Surface, ...")
              .Flag("selected", "Only the selected objects.").Opt("name", "TEXT", "Only objects whose name contains TEXT.")
              .Opt("bbox", "X0,Y0,Z0,X1,Y1,Z1", "Only objects whose bounding box touches this region.").Flag("inside", "With --bbox: only objects entirely inside it.")
              .Flag("summary", "Counts per type and layer, and the overall bounding box.").Opt("limit", "N", "Show at most N objects (default 100).").Opt("offset", "N", "Skip the first N objects (paging)."));

            cli.Register(new CommandSpec("info", "Objects", "info ID... | --selected | --name NAME", "Details of objects: full id, geometry, size, volume/area/length where they exist, visibility, material, user text.", a =>
            {
                var doc = Doc();
                var sb = new StringBuilder();
                foreach (var id in Targets(doc, a, true))
                {
                    var o = doc.Objects.FindId(id);
                    sb.AppendLine($"{id} {Line(doc, o).Substring(9)}");
                    if (!o.IsNormal || o.IsLocked || o.IsHidden) sb.AppendLine($"  state: {(o.IsHidden ? "hidden" : "")} {(o.IsLocked ? "locked" : "")}".TrimEnd());
                    var g = o.Geometry;
                    var box = g.GetBoundingBox(true);
                    if (box.IsValid) sb.AppendLine($"  size: {Fmt.N(box.Diagonal.X)} x {Fmt.N(box.Diagonal.Y)} x {Fmt.N(box.Diagonal.Z)}, center {Fmt.P(box.Center)}");
                    if (g is Curve c)
                    {
                        sb.AppendLine($"  curve: length {Fmt.N(c.GetLength())}, {(c.IsClosed ? "closed" : "open")}, {(c.IsPlanar() ? "planar" : "not planar")}, degree {c.Degree}");
                        sb.AppendLine($"  from {Fmt.P(c.PointAtStart)} to {Fmt.P(c.PointAtEnd)}");
                        if (c.TryGetPlane(out var cp)) sb.AppendLine($"  plane: origin {Fmt.P(cp.Origin)}, normal {Fmt.P(cp.Normal)}");
                    }
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
                    if (o.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromObject && o.Attributes.MaterialIndex >= 0)
                        sb.AppendLine($"  material: {doc.Materials[o.Attributes.MaterialIndex]?.Name}");
                    var keys = o.Attributes.GetUserStrings();
                    if (keys != null && keys.Count > 0) sb.AppendLine("  user text: " + string.Join("; ", keys.AllKeys.Select(k => k + "=" + keys[k])));
                }
                return CliResult.Ok(sb.ToString().TrimEnd());
            }).Flag("selected", "Describe the current selection.").Opt("name", "NAME", "Describe the objects with this exact name."));

            cli.Register(new CommandSpec("add", "Objects", "add KIND ARGS... [--at X,Y,Z] [--scale F] [--rotate DEG [--rotate-axis x|y|z]] [--move X,Y,Z] [--name N] [--layer L] [--color R,G,B] [--material M]", "Adds geometry and prints its id. KIND: point, line, polyline, curve, circle, arc, ellipse, box, sphere, cylinder, cone, surface (3-4 corner points), plane (a flat WIDTH x LENGTH rectangle). Solids and planes are centred on --at (default the origin). --scale, --rotate and --move are applied afterwards, about --at.", a =>
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
                    case "surface":
                        {
                            var corners = a.Positional.Skip(1).Select(t => Fmt.Point(t, "CORNER")).ToList();
                            if (corners.Count < 3 || corners.Count > 4) throw new CliException("surface needs 3 or 4 corner points, in order around the outline");
                            var srf = corners.Count == 3
                                ? NurbsSurface.CreateFromCorners(corners[0], corners[1], corners[2])
                                : NurbsSurface.CreateFromCorners(corners[0], corners[1], corners[2], corners[3]);
                            if (srf == null) throw new CliException("those corners do not make a surface (are they in a line?)");
                            id = doc.Objects.AddSurface(srf, attrs);
                            break;
                        }
                    case "plane":
                        {
                            var w = a.PosNumber(1, "WIDTH(x)"); var l = a.PosNumber(2, "LENGTH(y)");
                            var srf = new PlaneSurface(Plane.WorldXY, new Interval(-w / 2, w / 2), new Interval(-l / 2, l / 2));
                            srf.Translate(new Vector3d(at));
                            id = doc.Objects.AddSurface(srf, attrs);
                            break;
                        }
                    default:
                        throw new CliException($"unknown kind '{kind}' (point, line, polyline, curve, circle, arc, ellipse, box, sphere, cylinder, cone, surface, plane)");
                }
                if (id == Guid.Empty) throw new CliException("Rhino did not add the object (check the sizes)");
                if (a.Has("scale") || a.Has("rotate") || a.Has("move"))
                {
                    var pivot = a.Has("at") ? at : Point3d.Origin;
                    var rotAxis = (a.Opt("rotate-axis") ?? "z").ToLowerInvariant();
                    var rotDir = rotAxis == "x" ? Vector3d.XAxis : rotAxis == "y" ? Vector3d.YAxis : rotAxis == "z" ? Vector3d.ZAxis : throw new CliException("--rotate-axis is x, y or z");
                    var xf = Transform.Identity;
                    if (a.Has("scale")) xf = Transform.Scale(pivot, a.OptNumber("scale").Value) * xf;
                    if (a.Has("rotate")) xf = Transform.Rotation(RhinoMath.ToRadians(a.OptNumber("rotate").Value), rotDir, pivot) * xf;
                    if (a.Has("move")) xf = Transform.Translation(Fmt.Vector(a.Opt("move"), "--move")) * xf;
                    id = doc.Objects.Transform(id, xf, true);
                }
                doc.Views.Redraw();
                return CliResult.Ok($"added {kind} {id}");
            }).Opt("at", "X,Y,Z", "Centre / origin.").Opt("scale", "F", "Scale about --at after creating.").Opt("rotate", "DEG", "Rotate about --at after creating.")
              .Opt("rotate-axis", "x|y|z", "Axis for --rotate (default z).").Opt("move", "X,Y,Z", "Move by this vector after creating.").Opt("axis", "x|y|z", "Axis of circle, cylinder and cone (default z).").Flag("cap", "Cap the ends of a cylinder or cone.")
              .Flag("close", "Close a polyline.").Opt("degree", "N", "Degree of a curve through control points (default 3).").Opt("name", "N", "Object name.").Opt("layer", "L", "Layer (created if missing).").Opt("color", "R,G,B", "Object colour.").Opt("material", "M", "Material name (see `materials`).")
              .Example("add box 20 10 5 --at 0,0,2.5 --name base")
              .Example("add polyline 0,0 10,0 10,10 --close")
              .Example("add cylinder 3 12 --cap --axis x")
              .Example("add surface 0,0,0 10,0,0 10,10,3 0,10,3")
              .Example("add box 20 10 5 --at 0,0,0 --rotate 30 --move 0,0,10"));
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
            if (a.Has("material"))
            {
                var index = doc.Materials.Find(a.Opt("material"), true);
                if (index < 0) throw new CliException($"no material '{a.Opt("material")}' (`materials` lists them, `material-new` makes one)");
                attrs.MaterialIndex = index;
                attrs.MaterialSource = ObjectMaterialSource.MaterialFromObject;
            }
        }

        // ---------------------------------------------------------------- editing

        /// <summary>Transforms (or copies) an object.</summary>
        private static Guid Xf(RhinoDoc doc, Guid id, Transform x, bool copy)
        {
            return doc.Objects.Transform(id, x, !copy);
        }

        private static CommandSpec Edit(string name, string usage, string summary, Func<CliArgs, RhinoDoc, List<Guid>, CliResult> run) =>
            new CommandSpec(name, "Editing", usage, summary, a => run(a, Doc(), Targets(Doc(), a, false))).Flag("selected", "Act on the current selection instead of listing ids.");

        /// <summary>The objects a command acts on: --selected, --name (where the command allows it), else the listed ids.</summary>
        private static List<Guid> Targets(RhinoDoc doc, CliArgs a, bool byName)
        {
            if (a.Has("selected"))
            {
                var picked = doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList();
                if (picked.Count == 0) throw new CliException("nothing is selected");
                return picked;
            }
            if (byName && a.Has("name"))
            {
                var named = doc.Objects.Where(o => string.Equals(o.Name, a.Opt("name"), StringComparison.OrdinalIgnoreCase)).Select(o => o.Id).ToList();
                if (named.Count == 0) throw new CliException($"no object named '{a.Opt("name")}'");
                return named;
            }
            return Fmt.Ids(doc, a.Positional);
        }

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

            cli.Register(Edit("set", "set ID... [--name N] [--layer L] [--color R,G,B] [--material M]", "Changes name, layer, colour or material of objects.", (a, doc, ids) =>
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
            }).Opt("name", "N", "New name.").Opt("layer", "L", "Layer (created if missing).").Opt("color", "R,G,B", "Object colour.").Opt("material", "M", "Material name (see `materials`)."));

            cli.Register(new CommandSpec("select", "Editing", "select ID... | --all | --layer NAME | --name NAME | --type TYPE | --none [--add]", "Changes the selection (what the user sees highlighted). Replaces it unless --add. Filters add up.", a =>
            {
                var doc = Doc();
                if (!a.Has("add")) doc.Objects.UnselectAll();
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
              .Opt("type", "TYPE", "Select by object type: Brep, Extrusion, Curve, Point, Mesh, ...").Flag("none", "Clear the selection.").Flag("add", "Keep the current selection and add to it."));

            cli.Register(new CommandSpec("deselect", "Editing", "deselect [ID...]", "Removes objects from the selection; without ids, clears it.", a =>
            {
                var doc = Doc();
                if (a.Positional.Count == 0)
                {
                    var all = doc.Objects.UnselectAll();
                    doc.Views.Redraw();
                    return CliResult.Ok($"{all} object(s) deselected");
                }
                var n = Fmt.Ids(doc, a.Positional).Count(id => doc.Objects.FindId(id) is RhinoObject o && o.IsSelected(false) != 0 && doc.Objects.Select(id, false));
                doc.Views.Redraw();
                return CliResult.Ok($"{n} object(s) deselected");
            }));
        }

        // ---------------------------------------------------------------- materials

        private static void Materials(RhinoCli cli)
        {
            cli.Register(new CommandSpec("materials", "Materials", "materials [ID...]", "Lists the document's materials (name, colour), or with ids the material each object actually shows. Assign one with `set ID... --material NAME` or `add ... --material NAME`.", a =>
            {
                var doc = Doc();
                if (a.Positional.Count > 0)
                    return CliResult.Ok(string.Join("\n", Fmt.Ids(doc, a.Positional).Select(id =>
                    {
                        var o = doc.Objects.FindId(id);
                        var source = o.Attributes.MaterialSource;
                        var m = o.RenderMaterial;
                        var from = source == ObjectMaterialSource.MaterialFromObject ? "object" : source == ObjectMaterialSource.MaterialFromLayer ? "layer" : source.ToString();
                        return $"{Fmt.Short(id)} {(m == null ? "(default material)" : string.IsNullOrEmpty(m.Name) ? "(unnamed)" : m.Name)}  from {from}";
                    })));
                var lines = new List<string>();
                for (var i = 0; i < doc.Materials.Count; i++)
                {
                    var m = doc.Materials[i];
                    if (m == null || m.IsDeleted) continue;
                    lines.Add($"{(string.IsNullOrEmpty(m.Name) ? "(unnamed)" : m.Name)}  color={m.DiffuseColor.R},{m.DiffuseColor.G},{m.DiffuseColor.B}");
                }
                return CliResult.Ok(lines.Count == 0 ? "(no materials; `material-new` makes one)" : string.Join("\n", lines));
            }));

            cli.Register(new CommandSpec("material-new", "Materials", "material-new NAME [--color R,G,B] [--shine 0-1] [--transparency 0-1]", "Creates a material (a plain colour, for viewports and renders).", a =>
            {
                var doc = Doc();
                var name = a.Pos(0, "NAME");
                if (doc.Materials.Find(name, true) >= 0) throw new CliException($"a material named '{name}' exists already");
                var m = new Material { Name = name, DiffuseColor = a.Has("color") ? Fmt.Color(a.Opt("color"), "--color") : System.Drawing.Color.Gray };
                if (a.Has("shine")) m.Shine = Unit(a.OptNumber("shine").Value, "--shine") * Material.MaxShine;
                if (a.Has("transparency")) m.Transparency = Unit(a.OptNumber("transparency").Value, "--transparency");
                if (doc.Materials.Add(m) < 0) throw new CliException("Rhino did not add the material");
                doc.Views.Redraw();
                return CliResult.Ok("created material " + name);
            }).Opt("color", "R,G,B", "Diffuse colour (default grey).").Opt("shine", "0-1", "How glossy.").Opt("transparency", "0-1", "0 opaque, 1 clear."));
        }

        private static double Unit(double v, string what)
        {
            if (v < 0 || v > 1) throw new CliException($"{what} is between 0 and 1");
            return v;
        }

        // ---------------------------------------------------------------- viewing

        private static Rhino.Display.RhinoView FindView(RhinoDoc doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return doc.Views.ActiveView ?? throw new CliException("there is no active view");
            return doc.Views.FirstOrDefault(v => string.Equals(v.ActiveViewport.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliException($"no viewport '{name}' (`views` lists them)");
        }

        private static void Viewing(RhinoCli cli)
        {
            cli.Register(new CommandSpec("shot", "View", "shot [--view NAME] [--camera X,Y,Z --target X,Y,Z] [--lens MM | --parallel] [--display MODE] [--size WxH] [--zoom ID,ID...|selected] [--grid] [--axes]", "Takes a picture of a viewport and returns it. Restores the viewport afterwards.", a =>
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
                    if (a.Has("camera") || a.Has("target") || a.Has("lens") || a.Has("parallel") || a.Has("up"))
                    {
                        if (a.Has("parallel")) vp.ChangeToParallelProjection(true);
                        else if (!vp.IsPerspectiveProjection && (a.Has("lens") || a.Has("camera"))) vp.ChangeToPerspectiveProjection(true, a.OptNumber("lens") ?? 50);
                        var target = a.Has("target") ? Fmt.Point(a.Opt("target"), "--target") : vp.CameraTarget;
                        var camera = a.Has("camera") ? Fmt.Point(a.Opt("camera"), "--camera") : vp.CameraLocation;
                        if (camera.DistanceTo(target) < RhinoMath.ZeroTolerance) throw new CliException("--camera and --target are the same point");
                        if (a.Has("up")) vp.CameraUp = Fmt.Vector(a.Opt("up"), "--up");
                        vp.SetCameraLocations(target, camera);
                        if (a.Has("lens") && vp.IsPerspectiveProjection) vp.Camera35mmLensLength = a.OptNumber("lens").Value;
                    }
                    if (a.Has("display"))
                    {
                        var mode = Rhino.Display.DisplayModeDescription.FindByName(a.Opt("display"));
                        if (mode == null)
                            throw new CliException($"no display mode '{a.Opt("display")}'. Available: " + string.Join(", ", Rhino.Display.DisplayModeDescription.GetDisplayModes().Select(m => m.EnglishName)));
                        vp.DisplayMode = mode;
                    }
                    var targets = a.Has("zoom") ? (a.Opt("zoom") == "selected" ? doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList() : Fmt.Ids(doc, a.Opt("zoom").Split(','))) : null;
                    if (targets != null && targets.Count > 0)
                    {
                        var box = BoundingBox.Empty;
                        foreach (var id in targets) box.Union(doc.Objects.FindId(id).Geometry.GetBoundingBox(true));
                        vp.ZoomBoundingBox(box);
                    }
                    else if (a.Has("view")) vp.ZoomExtents();
                    view.Redraw();

                    var capture = new Rhino.Display.ViewCapture { Width = w, Height = h, ScaleScreenItems = false, DrawAxes = a.Has("axes"), DrawGrid = a.Has("grid"), DrawGridAxes = a.Has("grid"), TransparentBackground = false };
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
              .Opt("camera", "X,Y,Z", "Camera position (with --target; either alone keeps the other as it is).").Opt("target", "X,Y,Z", "Point the camera looks at.")
              .Opt("up", "X,Y,Z", "Camera up vector.").Opt("lens", "MM", "35mm-equivalent lens length for a perspective view (default 50 when switching from parallel).").Flag("parallel", "Parallel (orthographic) projection.")
              .Flag("grid", "Draw the construction grid.").Flag("axes", "Draw the world axes.")
              .Opt("display", "MODE", "A display mode, e.g. Shaded, Wireframe, Rendered, Ghosted, Arctic.")
              .Opt("size", "WxH", "Pixels, default 1000x700.").Opt("zoom", "ID,ID...|selected", "Frame these objects (comma-separated ids) or the selection first.")
              .Example("shot --view top --display Wireframe")
              .Example("shot --view iso --zoom 3fa85f64,9b1c0d22")
              .Example("shot --camera 80,-60,40 --target 0,0,0 --lens 85"));

            cli.Register(new CommandSpec("views", "View", "views", "Lists the viewports: display mode, projection, lens and camera position/target.", a =>
            {
                var doc = Doc();
                return CliResult.Ok(string.Join("\n", doc.Views.Select(v =>
                {
                    var vp = v.ActiveViewport;
                    var lens = vp.IsPerspectiveProjection ? $"perspective {Fmt.N(vp.Camera35mmLensLength)}mm" : "parallel";
                    return $"{vp.Name}{(v == doc.Views.ActiveView ? " *" : "")}  {vp.DisplayMode.EnglishName}  {lens}  camera {Fmt.P(vp.CameraLocation)} -> {Fmt.P(vp.CameraTarget)}  up {Fmt.P(vp.CameraUp)}  size {v.ClientRectangle.Width}x{v.ClientRectangle.Height}";
                })) + "\n(* = active)");
            }));

            cli.Register(new CommandSpec("zoom", "View", "zoom [ID...|selected] [--viewport NAME]", "Frames objects, the selection, or everything (no arguments) in a viewport. Unlike `shot --zoom` this changes what the user sees.", a =>
            {
                var doc = Doc();
                var view = FindView(doc, a.Opt("viewport"));
                if (a.Positional.Count == 0) view.ActiveViewport.ZoomExtents();
                else
                {
                    var ids = a.Positional.Count == 1 && a.Positional[0] == "selected"
                        ? doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList()
                        : Fmt.Ids(doc, a.Positional);
                    if (ids.Count == 0) throw new CliException("nothing is selected");
                    var box = BoundingBox.Empty;
                    foreach (var id in ids) box.Union(doc.Objects.FindId(id).Geometry.GetBoundingBox(true));
                    view.ActiveViewport.ZoomBoundingBox(box);
                }
                view.Redraw();
                return CliResult.Ok("zoomed " + view.ActiveViewport.Name);
            }).Opt("viewport", "NAME", "Viewport to change (default the active one)."));

            cli.Register(new CommandSpec("named-views", "View", "named-views [save NAME | restore NAME | rm NAME] [--viewport NAME]", "Saved cameras (kept in the file). Without arguments, lists them. `save` replaces one of the same name; `restore` applies it to the viewport.", a =>
            {
                var doc = Doc();
                if (a.Positional.Count == 0)
                {
                    var lines = new List<string>();
                    for (var i = 0; i < doc.NamedViews.Count; i++)
                    {
                        var nv = doc.NamedViews[i];
                        if (nv == null) continue;
                        var vp = nv.Viewport;
                        lines.Add(vp == null ? nv.Name : $"{nv.Name}  {(vp.IsPerspectiveProjection ? "perspective " + Fmt.N(vp.Camera35mmLensLength) + "mm" : "parallel")}  camera {Fmt.P(vp.CameraLocation)} -> {Fmt.P(vp.TargetPoint)}");
                    }
                    return CliResult.Ok(lines.Count == 0 ? "(no named views)" : string.Join("\n", lines));
                }
                var action = a.Positional[0].ToLowerInvariant();
                var name = a.Pos(1, "NAME");
                switch (action)
                {
                    case "save":
                        {
                            var view = FindView(doc, a.Opt("viewport"));
                            if (doc.NamedViews.FindByName(name) >= 0) doc.NamedViews.Delete(name);
                            if (doc.NamedViews.Add(name, view.ActiveViewport.Id) < 0) throw new CliException("Rhino did not save the view");
                            return CliResult.Ok($"saved named view '{name}'");
                        }
                    case "restore":
                        {
                            var index = doc.NamedViews.FindByName(name);
                            if (index < 0) throw new CliException($"no named view '{name}'");
                            var view = FindView(doc, a.Opt("viewport"));
                            // Rhino renames the viewport to the view's name; the user's viewport keeps its own
                            var original = view.ActiveViewport.Name;
                            if (!doc.NamedViews.Restore(index, view.ActiveViewport)) throw new CliException("Rhino could not restore the view");
                            if (!string.IsNullOrEmpty(original) && view.ActiveViewport.Name != original) view.ActiveViewport.Name = original;
                            view.Redraw();
                            return CliResult.Ok($"restored named view '{name}' in {original}");
                        }
                    case "rm":
                        if (doc.NamedViews.FindByName(name) < 0) throw new CliException($"no named view '{name}'");
                        doc.NamedViews.Delete(name);
                        return CliResult.Ok($"removed named view '{name}'");
                    default:
                        throw new CliException($"unknown action '{action}' (save, restore, rm)");
                }
            }).Opt("viewport", "NAME", "Viewport to save from or restore into (default the active one)."));
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

        private static string PyWord(string text, string what)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^\w+$")) throw new CliException($"{what}: letters, digits and _ only");
            return text;
        }

        /// <summary>Runs Python in Rhino and returns what it passed to emit(). A file carries the answer, because the command history may miss output.</summary>
        private static string PythonQuery(string body)
        {
            Doc(); // fail early when nothing is open
            var outFile = Path.Combine(Path.GetTempPath(), "rhinocc-out-" + Guid.NewGuid().ToString("N") + ".txt");
            var scriptFile = Path.ChangeExtension(outFile, ".py");
            var code = new StringBuilder();
            code.Append("# -*- coding: utf-8 -*-\nimport io\n_o = io.open(r\"").Append(outFile).Append("\", \"w\", encoding=\"utf-8\")\n")
                .Append("def emit(s):\n    if not isinstance(s, type(u\"\")):\n        s = s.decode(\"utf-8\", \"replace\")\n    _o.write(s + u\"\\n\")\n")
                .Append("try:\n");
            foreach (var line in body.Split('\n')) code.Append("    ").Append(line).Append('\n');
            code.Append("except Exception as e:\n    emit(\"error: %r\" % (e,))\nfinally:\n    _o.close()\n");
            File.WriteAllText(scriptFile, code.ToString());
            try
            {
                var before = History();
                RhinoApp.RunScript($"-_RunPythonScript \"{scriptFile}\"", false);
                if (!File.Exists(outFile)) throw new CliException("the Python script did not run" + (NewHistory(before).Length == 0 ? "" : ":\n" + NewHistory(before)));
                return File.ReadAllText(outFile, Encoding.UTF8).TrimEnd();
            }
            finally
            {
                try { File.Delete(scriptFile); } catch { /* temp file */ }
                try { File.Delete(outFile); } catch { /* temp file */ }
            }
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

            cli.Register(new CommandSpec("py-funcs", "Scripting", "py-funcs [FILTER]", "Lists the rhinoscriptsyntax functions of this Rhino whose name contains FILTER (letters/digits, e.g. `curve`, `surface`, `boolean`). Find the function before you script it.", a =>
            {
                var filter = a.Positional.Count > 0 ? PyWord(a.Positional[0], "FILTER") : "";
                return CliResult.Ok(PythonQuery(
                    "import rhinoscriptsyntax as rs\n" +
                    $"f = \"{filter}\".lower()\n" +
                    "names = sorted(n for n in dir(rs) if not n.startswith(\"_\") and callable(getattr(rs, n, None)) and f in n.lower())\n" +
                    "emit(\", \".join(names[:200]))\n" +
                    "emit(\"(%d functions%s)\" % (len(names), \"; showing 200, add a FILTER to narrow\" if len(names) > 200 else \"\"))"));
            }).Example("py-funcs curve"));

            cli.Register(new CommandSpec("py-help", "Scripting", "py-help FUNCTION", "The documentation of one rhinoscriptsyntax function: arguments, return value, example. Read it before using a function you are not sure of.", a =>
            {
                var name = PyWord(a.Pos(0, "FUNCTION"), "FUNCTION");
                var text = PythonQuery(
                    "import rhinoscriptsyntax as rs\n" +
                    $"w = \"{name}\".lower()\n" +
                    "hit = [n for n in dir(rs) if n.lower() == w and callable(getattr(rs, n, None))]\n" +
                    "if hit:\n" +
                    "    emit(hit[0])\n" +
                    "    emit((getattr(rs, hit[0]).__doc__ or \"(no documentation)\").strip())\n" +
                    "else:\n" +
                    "    near = sorted(n for n in dir(rs) if w in n.lower() and not n.startswith(\"_\"))\n" +
                    "    emit(\"no function '%s'.\" % w + (\" Similar: \" + \", \".join(near[:30]) if near else \" `py-funcs FILTER` searches by name.\"))");
                return CliResult.Ok(text.Length > 6000 ? text.Substring(0, 6000) + "\n… (cut)" : text);
            }).Example("py-help AddLoftSrf"));

            cli.Register(new CommandSpec("log", "Scripting", "log [N]", "The last N (default 30) lines of Rhino's command history.", a =>
            {
                var n = a.Positional.Count > 0 ? (int)a.PosNumber(0, "N") : 30;
                var lines = History().Replace("\r\n", "\n").Split('\n');
                return CliResult.Ok(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n))));
            }));
        }
    }
}
