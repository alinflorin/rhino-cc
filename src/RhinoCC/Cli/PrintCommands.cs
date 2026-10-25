using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Geometry;
using RhinoCC.Core.Printing;

namespace RhinoCC.Cli
{
    /// <summary>
    /// `print`, `orient`, `supports…` and `sprue`: the print settings of the model, the print orientation, resin supports
    /// and the casting sprue, all worked out on the Rhino geometry itself (no STL).
    /// </summary>
    internal static class PrintCommands
    {
        private sealed class Key
        {
            public string Name, Help;
            public Func<PrintSettings, string> Get;
            public Action<PrintSettings, string> Set;
        }

        private static Key Num(string name, string help, Func<PrintSettings, double> get, Action<PrintSettings, double> set) => new Key
        {
            Name = name, Help = help, Get = s => Fmt.N(get(s)), Set = (s, v) => set(s, CliArgs.Number(v, name)),
        };

        private static Key Flag(string name, string help, Func<PrintSettings, bool> get, Action<PrintSettings, bool> set) => new Key
        {
            Name = name, Help = help, Get = s => get(s) ? "on" : "off", Set = (s, v) => set(s, Fmt.OnOff(v, name)),
        };

        private static readonly Key[] Keys =
        {
            new Key { Name = "metal", Help = "metal for weights: " + Metals.Names, Get = s => s.Metal, Set = (s, v) => s.Metal = v.ToLowerInvariant() },
            new Key { Name = "process", Help = "casting (resin master for lost-wax casting) or direct", Get = s => s.Process, Set = (s, v) => s.Process = v.ToLowerInvariant() },
            Num("min-wall", "thinnest acceptable wall, mm (default for `analyze`)", s => s.MinWall, (s, v) => s.MinWall = v),
            Num("tilt", "print orientation: degrees about X. With supports on, setting it TURNS the piece in Rhino (about its centre) and sets it on the plate, z = 0 (30 usual, 45 sometimes); tilt=0 turns it back", s => s.Tilt, (s, v) => s.Tilt = v),
            new Key { Name = "notes", Help = "free text about the piece", Get = s => s.Notes, Set = (s, v) => s.Notes = v },
            Flag("supports.enabled", "use supports (off: nothing is generated, the points are hidden)", s => s.Supports.Enabled, (s, v) => s.Supports.Enabled = v),
            Num("supports.tip-radius", "radius where a pillar touches the piece, mm (0.1-1)", s => s.Supports.TipRadius, (s, v) => s.Supports.TipRadius = v),
            Num("supports.base-radius", "radius where a pillar meets the plate or raft, mm (0.2-2)", s => s.Supports.BaseRadius, (s, v) => s.Supports.BaseRadius = v),
            Num("supports.overhang-angle", "faces pointing down within this many degrees of horizontal need support (0-80)", s => s.Supports.OverhangAngle, (s, v) => s.Supports.OverhangAngle = v),
            Num("supports.spacing", "largest distance one automatic support may serve, mm (1-10)", s => s.Supports.Spacing, (s, v) => s.Supports.Spacing = v),
            Num("supports.raft-radius", "how far the raft reaches around each pillar, mm (0-10)", s => s.Supports.RaftRadius, (s, v) => s.Supports.RaftRadius = v),
            Num("supports.raft-thickness", "raft thickness, mm; 0 = no raft; below lift - 0.1", s => s.Supports.RaftThickness, (s, v) => s.Supports.RaftThickness = v),
            Num("supports.lift", "gap between the piece and the plate, mm (1-10)", s => s.Supports.Lift, (s, v) => s.Supports.Lift = v),
            Flag("sprue.enabled", "put a casting sprue on the piece", s => s.Sprue.Enabled, (s, v) => s.Sprue.Enabled = v),
            Num("sprue.diameter", "sprue diameter, mm (0.5-6; about 2)", s => s.Sprue.Diameter, (s, v) => s.Sprue.Diameter = v),
            Num("sprue.length", "sprue length, mm (1-30)", s => s.Sprue.Length, (s, v) => s.Sprue.Length = v),
        };

        public static void Register(RhinoCli cli)
        {
            cli.Register(new CommandSpec("print", "Printing",
                "print",
                "The print settings stored in this model: metal and process, minimum wall, print orientation (tilt), supports and sprue, with what each key means, and whether the piece fits the printer as oriented.", a =>
            {
                var doc = RhinoCommands.Doc();
                var s = PrintOps.Settings(doc);
                var sb = new StringBuilder();
                sb.AppendLine($"printer {s.Printer} (build volume {Fmt.N(BuildVolume.X)} x {Fmt.N(BuildVolume.Y)} x {Fmt.N(BuildVolume.Z)} mm); settings are in mm, whatever the model's units");
                foreach (var k in Keys) sb.AppendLine($"  {k.Name} = {k.Get(s)}    {k.Help}");
                if (s.Sprue.At != null) sb.AppendLine($"  sprue spot (by hand): {Fmt.P(new Rhino.Geometry.Point3d(s.Sprue.At[0], s.Sprue.At[1], s.Sprue.At[2]))} (model units)");
                try
                {
                    var piece = PrintOps.Gather(doc, PrintOps.DefaultTargets(doc));
                    var f = RhinoCC.Core.Printing.Supports.Frame(piece.Mesh, 0, s.Supports.Lift);
                    sb.Append($"as oriented ({Fmt.N(piece.Applied)}° applied tilt, {Fmt.N(s.Supports.Lift)} mm lift) the piece needs {Fmt.N(f.Size.X)} x {Fmt.N(f.Size.Y)} x {Fmt.N(f.Size.Z)} mm: {(f.Fits ? "fits" : "DOES NOT FIT")} the printer");
                }
                catch (CliException e) { sb.Append("(" + e.Message + ")"); }
                return CliResult.Ok(sb.ToString());
            }));

            cli.Register(new CommandSpec("print-set", "Printing",
                "print-set KEY=VALUE...",
                "Changes print settings (keys are listed by `print`). Supports and sprue follow at once: pillars are rebuilt, the sprue is moved or removed. Several keys can be set in one call; if any value is invalid nothing changes.", a =>
            {
                var doc = RhinoCommands.Doc();
                if (a.Positional.Count == 0) throw new CliException("nothing to set");
                var before = PrintOps.Settings(doc);
                var after = before.Clone();
                var done = new List<string>();
                foreach (var pair in a.Positional)
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) throw new CliException($"'{pair}' is not KEY=VALUE");
                    var name = pair.Substring(0, eq).Trim();
                    var key = Keys.FirstOrDefault(k => k.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (key == null) throw new CliException($"unknown key '{name}' (keys: {string.Join(", ", Keys.Select(k => k.Name))})");
                    key.Set(after, pair.Substring(eq + 1).Trim());
                    done.Add($"{key.Name}={key.Get(after)}");
                }
                var problem = after.Validate();
                if (problem != null) throw new CliException(problem + " (nothing was changed)");
                PrintOps.SaveSettings(doc, after);
                var follow = PrintOps.Follow(doc, before, after);
                return CliResult.Ok("set " + string.Join(", ", done) + (follow.Length > 0 ? "; " + follow : ""));
            }).Example("print-set tilt=30 supports.enabled=on").Example("print-set sprue.enabled=on sprue.diameter=2"));

            AddTargets(cli.Register2(new CommandSpec("orient", "Printing",
                "orient [ID... | --selected | --layer NAME | --name NAME] [--min DEG] [--max DEG] [--step DEG]",
                "Ranks print orientations (tilt about X from the piece's original orientation, default -60..60 in 5° steps) by the overhanging area that would need supports plus the flat area lying on the plate, and says which fit the printer. Purely geometric: it cannot see which side carries the fine detail, so judge it against the rules (fine detail faces up). Apply one with `print-set tilt=N`.", a =>
            {
                var doc = RhinoCommands.Doc();
                var s = PrintOps.Settings(doc);
                var min = a.OptNumber("min") ?? -60; var max = a.OptNumber("max") ?? 60; var step = a.OptNumber("step") ?? 5;
                if (step < 1 || max < min) throw new CliException("--step must be at least 1 and --max not below --min");
                var piece = PrintOps.Gather(doc, Targets(doc, a));
                var rows = RhinoCC.Core.Printing.Supports.RankTilts(piece.Canonical(), min, max, step, s.Supports.OverhangAngle, s.Supports.Lift);
                var sb = new StringBuilder();
                sb.AppendLine($"tilt about X, overhang within {Fmt.N(s.Supports.OverhangAngle)}° of facing down; areas in mm², size in mm (lift {Fmt.N(s.Supports.Lift)} mm included); applied tilt {Fmt.N(piece.Applied)}°");
                foreach (var r in rows.Take(6))
                    sb.AppendLine($"  {Fmt.N(r.Tilt),6}°  overhang {Fmt.N(r.Overhang),9}  flat on plate {Fmt.N(r.Flat),9}  size {Fmt.N(r.Size.X)} x {Fmt.N(r.Size.Y)} x {Fmt.N(r.Size.Z)}  {(r.Fits ? "fits" : "does not fit")}");
                var now = rows.FirstOrDefault(r => Math.Abs(r.Tilt - piece.Applied) < 1e-9);
                if (now != null && !rows.Take(6).Contains(now)) sb.AppendLine($"  (current {Fmt.N(now.Tilt)}°: overhang {Fmt.N(now.Overhang)}, flat {Fmt.N(now.Flat)})");
                sb.Append("apply (turns the piece, needs supports on) with: print-set tilt=" + Fmt.N(rows[0].Tilt));
                return CliResult.Ok(sb.ToString());
            })).Example("orient --min 0 --max 60"));

            cli.Register(new CommandSpec("supports", "Printing",
                "supports",
                "Lists the supports (the contact points on the `support-points` layer, in model units): id, auto or manual, position, own tip radius. `*` = manual. Also the printed size and whether it fits. The points are ordinary Rhino points: the user can move, copy or delete them; `supports-build` regenerates the pillars.", a =>
            {
                var doc = RhinoCommands.Doc();
                var s = PrintOps.Settings(doc);
                var toMm = PrintOps.ToMm(doc);
                var list = PrintOps.ReadSupports(doc);
                var sb = new StringBuilder();
                sb.AppendLine($"supports {(s.Supports.Enabled ? "on" : "OFF")}, tilt set {Fmt.N(s.Tilt)}° applied {Fmt.N(PrintOps.AppliedTilt(doc))}°, {list.Count} contact(s): {list.Count(x => x.Source == "auto")} auto, {list.Count(x => x.Source == "manual")} manual (*)");
                foreach (var x in list)
                    sb.AppendLine($"  {x.Id}{(x.Source == "manual" ? "*" : " ")}  {Fmt.N(x.P.X / toMm)},{Fmt.N(x.P.Y / toMm)},{Fmt.N(x.P.Z / toMm)}{(x.Tip.HasValue ? "  tip " + Fmt.N(x.Tip.Value) + " mm" : "")}");
                try
                {
                    var piece = PrintOps.Gather(doc, PrintOps.DefaultTargets(doc));
                    var f = RhinoCC.Core.Printing.Supports.Frame(piece.Mesh, 0, s.Supports.Lift);
                    sb.Append($"printed size {Fmt.N(f.Size.X)} x {Fmt.N(f.Size.Y)} x {Fmt.N(f.Size.Z)} mm: {(f.Fits ? "fits" : "DOES NOT FIT")} the printer");
                }
                catch (CliException e) { sb.Append("(" + e.Message + ")"); }
                return CliResult.Ok(sb.ToString());
            }));

            AddTargets(cli.Register2(new CommandSpec("supports-auto", "Printing",
                "supports-auto [ID... | --selected | --layer NAME | --name NAME] [--angle DEG] [--spacing MM]",
                "Places a minimal set of supports under the overhangs of the piece for the current tilt, keeping the manual ones, then builds pillars and raft as solids on the `supports` layer. Needs supports.enabled=on. Review the result (`shot`, `supports`) and remove the bad ones: it does not know which faces are visible or carry detail.", a =>
            {
                var doc = RhinoCommands.Doc();
                var s = RequireSupports(doc);
                var auto = new AutoParams { Angle = s.Supports.OverhangAngle, Spacing = s.Supports.Spacing };
                if (a.Has("angle")) auto.Angle = a.OptNumber("angle").Value;
                if (a.Has("spacing")) auto.Spacing = a.OptNumber("spacing").Value;
                if (auto.Angle < 0 || auto.Angle > 80) throw new CliException("--angle must be between 0 and 80");
                if (auto.Spacing < 1 || auto.Spacing > 10) throw new CliException("--spacing must be between 1 and 10 mm");
                var piece = PrintOps.Gather(doc, Targets(doc, a));
                var existing = PrintOps.ReadSupports(doc);
                var manual = existing.Where(x => x.Source == "manual").ToList();
                var r = RhinoCC.Core.Printing.Supports.Auto(piece.Mesh, PrintOps.Params(s), auto, manual, PrintOps.NextId(existing));
                if (r == null) throw new CliException("the piece has no geometry to support");
                var rec = doc.BeginUndoRecord("RhinoCC supports");
                try
                {
                    PrintOps.WriteSupports(doc, r.Supports);
                    var built = PrintOps.BuildSupports(doc, piece, s);
                    doc.Views.Redraw();
                    PrintOps.RaiseChanged();
                    return CliResult.Ok($"{r.Stats.Added} support(s) added, {r.Stats.Kept} manual kept, {r.Supports.Count} in all; {built.Key} pillar(s), {built.Value} raft piece(s) built. " +
                        $"{r.Stats.OverhangFaces} overhanging face(s); {r.Stats.Blocked} spot(s) skipped because the piece is in the way below them." +
                        (PrintFits(piece, s)));
                }
                finally { doc.EndUndoRecord(rec); }
            }).Opt("angle", "DEG", "Overhang limit for this run (default: the setting).").Opt("spacing", "MM", "Spacing for this run (default: the setting).")));

            cli.Register(new CommandSpec("supports-add", "Printing",
                "supports-add X,Y,Z... [--tip MM] [--snap]",
                "Adds manual supports at points (model units). With --snap each point moves to the closest point of the piece's surface. Needs supports.enabled=on. Rebuilds the pillars.", a =>
            {
                var doc = RhinoCommands.Doc();
                var s = RequireSupports(doc);
                if (a.Positional.Count == 0) throw new CliException("missing point(s) X,Y,Z");
                var tip = a.OptNumber("tip");
                if (tip.HasValue && (tip < 0.1 || tip > 1)) throw new CliException("--tip must be between 0.1 and 1 mm");
                var piece = PrintOps.Gather(doc, PrintOps.DefaultTargets(doc));
                var list = PrintOps.ReadSupports(doc);
                var id = PrintOps.NextId(list);
                var added = new List<int>();
                foreach (var text in a.Positional)
                {
                    var p = Fmt.Point(text, "point");
                    var v = new Vec3(p.X * piece.ToMm, p.Y * piece.ToMm, p.Z * piece.ToMm);
                    if (a.Has("snap")) v = PrintOps.Snap(piece.Mesh, v);
                    list.Add(new Support { Id = id, P = v, Source = "manual", Tip = tip });
                    added.Add(id++);
                }
                var rec = doc.BeginUndoRecord("RhinoCC supports");
                try
                {
                    PrintOps.WriteSupports(doc, list);
                    var built = PrintOps.BuildSupports(doc, piece, s);
                    doc.Views.Redraw();
                    PrintOps.RaiseChanged();
                    return CliResult.Ok($"added support {string.Join(", ", added)}; {list.Count} in all, {built.Key} pillar(s) built");
                }
                finally { doc.EndUndoRecord(rec); }
            }).Opt("tip", "MM", "Own tip radius for these supports.").Flag("snap", "Move each point to the closest point of the piece's surface."));

            cli.Register(new CommandSpec("supports-rm", "Printing",
                "supports-rm ID... | --auto | --all",
                "Removes supports by id (see `supports`), or all automatic ones, or all of them, and rebuilds the pillars.", a =>
            {
                var doc = RhinoCommands.Doc();
                var s = PrintOps.Settings(doc);
                var list = PrintOps.ReadSupports(doc);
                HashSet<int> doomed;
                if (a.Has("all")) doomed = new HashSet<int>(list.Select(x => x.Id));
                else if (a.Has("auto")) doomed = new HashSet<int>(list.Where(x => x.Source == "auto").Select(x => x.Id));
                else
                {
                    if (a.Positional.Count == 0) throw new CliException("give support ids, or --auto, or --all");
                    var ids = a.Positional.Select(t => (int)CliArgs.Number(t, "support id")).ToList();
                    var missing = ids.Where(i => list.All(x => x.Id != i)).ToList();
                    if (missing.Count > 0) throw new CliException("no support with id " + string.Join(", ", missing));
                    doomed = new HashSet<int>(ids);
                }
                var rec = doc.BeginUndoRecord("RhinoCC supports");
                try
                {
                    var rest = list.Where(x => !doomed.Contains(x.Id)).ToList();
                    PrintOps.WriteSupports(doc, rest);
                    var built = 0;
                    if (s.Supports.Enabled)
                    {
                        try { built = PrintOps.BuildSupports(doc, PrintOps.Gather(doc, PrintOps.DefaultTargets(doc)), s).Key; }
                        catch (CliException) { PrintOps.ClearSupportSolids(doc); }
                    }
                    doc.Views.Redraw();
                    PrintOps.RaiseChanged();
                    return CliResult.Ok($"removed {doomed.Count}; {rest.Count} left, {built} pillar(s) built");
                }
                finally { doc.EndUndoRecord(rec); }
            }).Flag("auto", "Remove all automatic supports (manual ones stay).").Flag("all", "Remove every support."));

            AddTargets(cli.Register2(new CommandSpec("supports-build", "Printing",
                "supports-build [ID... | --selected | --layer NAME | --name NAME]",
                "Regenerates the pillars and the raft from the points on the `support-points` layer and the current settings. Run it after the user moved, added or deleted points by hand, or after the piece moved. Needs supports.enabled=on.", a =>
            {
                var doc = RhinoCommands.Doc();
                var s = RequireSupports(doc);
                var piece = PrintOps.Gather(doc, Targets(doc, a));
                var rec = doc.BeginUndoRecord("RhinoCC supports");
                try
                {
                    var built = PrintOps.BuildSupports(doc, piece, s);
                    doc.Views.Redraw();
                    PrintOps.RaiseChanged();
                    return CliResult.Ok($"{built.Key} pillar(s), {built.Value} raft piece(s) built" + PrintFits(piece, s));
                }
                finally { doc.EndUndoRecord(rec); }
            })));

            AddTargets(cli.Register2(new CommandSpec("sprue", "Printing",
                "sprue [ID... | --selected | --layer NAME | --name NAME] [--at X,Y,Z | --auto] [--diameter MM] [--length MM] [--off]",
                "Puts the casting sprue (wax-channel stub) on the piece as a solid on the `sprue` layer and reports where it joins, the metal thickness there against its diameter, and thin sections too far from it to fill. Without --at it picks the thickest, least visible spot with free air in front; --at snaps to the closest surface point. Run again to move or resize it; --off removes it. Only when the user wants a sprue.", a =>
            {
                var doc = RhinoCommands.Doc();
                var before = PrintOps.Settings(doc);
                var s = before.Clone();
                if (a.Has("off"))
                {
                    s.Sprue.Enabled = false;
                    PrintOps.SaveSettings(doc, s);
                    PrintOps.Follow(doc, before, s);
                    return CliResult.Ok("sprue removed");
                }
                s.Sprue.Enabled = true;
                if (a.Has("diameter")) s.Sprue.Diameter = a.OptNumber("diameter").Value;
                if (a.Has("length")) s.Sprue.Length = a.OptNumber("length").Value;
                if (a.Has("auto")) s.Sprue.At = null;
                if (a.Has("at")) { var p = Fmt.Point(a.Opt("at"), "--at"); s.Sprue.At = new[] { p.X, p.Y, p.Z }; }
                var problem = s.Validate();
                if (problem != null) throw new CliException(problem);
                var piece = PrintOps.Gather(doc, Targets(doc, a));
                var rec = doc.BeginUndoRecord("RhinoCC sprue");
                try
                {
                    var site = PrintOps.BuildSprue(doc, piece, s);
                    if (site == null) throw new CliException("no free spot for a sprue on this piece (every candidate runs into the piece); try --at");
                    PrintOps.SaveSettings(doc, s);
                    doc.Views.Redraw();
                    var toMm = piece.ToMm;
                    var sb = new StringBuilder();
                    sb.AppendLine($"sprue {Fmt.N(s.Sprue.Diameter)} mm x {Fmt.N(s.Sprue.Length)} mm at {Fmt.N(site.At.X / toMm)},{Fmt.N(site.At.Y / toMm)},{Fmt.N(site.At.Z / toMm)} ({(site.Manual ? "by hand" : "automatic")}), pointing {Fmt.N(site.Normal.X)},{Fmt.N(site.Normal.Y)},{Fmt.N(site.Normal.Z)}");
                    var good = Sprue.GoodRatio(s.Sprue.Diameter, site.Thickness, out var ratio);
                    sb.AppendLine($"metal behind the join {Fmt.N(site.Thickness)} mm; sprue/section ratio {Fmt.N(ratio)} ({(good ? "good" : $"outside {Sprue.RatioLow}-{Sprue.RatioHigh}: change the diameter or the spot")})");
                    var reach = Sprue.CheckReach(piece.Mesh, site.At);
                    sb.Append(reach.Regions == 0
                        ? $"reach: no thin section farther than {Sprue.Reach} mm from the join"
                        : $"reach: {reach.Regions} thin region(s) farther than {Sprue.Reach} mm from the join (farthest {Fmt.N(reach.Farthest)} mm), e.g. at {string.Join(" ", reach.Where.Take(4).Select(w => $"({Fmt.N(w.X / toMm)},{Fmt.N(w.Y / toMm)},{Fmt.N(w.Z / toMm)})"))}: the metal may freeze before it gets there");
                    return CliResult.Ok(sb.ToString());
                }
                finally { doc.EndUndoRecord(rec); }
            }).Opt("at", "X,Y,Z", "Put the sprue at the closest surface point to this point (model units).").Flag("auto", "Forget a manual spot and place it automatically.")
              .Opt("diameter", "MM", "Sprue diameter (default: the setting, 2).").Opt("length", "MM", "Sprue length (default: the setting, 5).").Flag("off", "Remove the sprue.")));
        }

        private static PrintSettings RequireSupports(RhinoDoc doc)
        {
            var s = PrintOps.Settings(doc);
            if (!s.Supports.Enabled) throw new CliException("supports are turned off; enable them first with `print-set supports.enabled=on` (only if the user asked for supports)");
            // the piece must really stand at the set tilt before supports are worked out
            if (Math.Abs(s.Tilt - PrintOps.AppliedTilt(doc)) > 1e-9) PrintOps.Follow(doc, s, s);
            return PrintOps.Settings(doc);
        }

        private static string PrintFits(Piece piece, PrintSettings s)
        {
            var f = RhinoCC.Core.Printing.Supports.Frame(piece.Mesh, 0, s.Supports.Lift);
            return f == null ? "" : $" Printed size {Fmt.N(f.Size.X)} x {Fmt.N(f.Size.Y)} x {Fmt.N(f.Size.Z)} mm: {(f.Fits ? "fits" : "DOES NOT FIT")} the printer.";
        }

        /// <summary>The same target options as `analyze`.</summary>
        private static CommandSpec AddTargets(CommandSpec c) =>
            c.Flag("selected", "Work on the current selection.").Opt("layer", "NAME", "Only this layer (full path).").Opt("name", "NAME", "Only objects with this exact name.");

        private static IList<RhinoObject> Targets(RhinoDoc doc, CliArgs a) =>
            AnalyzeCommand.Select(doc, a).Where(o => !PrintOps.IsOurs(doc, o)).ToList();

        // `Register` returns nothing; commands that take targets are wrapped so options can be chained on
        private static CommandSpec Register2(this RhinoCli cli, CommandSpec spec)
        {
            cli.Register(spec);
            return spec;
        }
    }
}
