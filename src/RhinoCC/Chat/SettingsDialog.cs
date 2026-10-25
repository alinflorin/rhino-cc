using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using RhinoCC.Cli;
using RhinoCC.Core.Agents;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Printing;
using PrintSettings = RhinoCC.Core.Printing.PrintSettings;

namespace RhinoCC.Chat
{
    /// <summary>
    /// Settings, laid out like frederic-coultier's: pages on the left, one page on the right, and no OK button: every valid change
    /// is applied as it is made. The first four pages belong to the open model (stored in the .3dm, and the supports and the sprue
    /// follow them live); the others are the plugin's own.
    /// </summary>
    internal sealed class SettingsDialog : Dialog
    {
        private sealed class SupportRow
        {
            public int Id;
            public string Source = "manual";
            public string X, Y, Z;
            public string Label => Id + (Source == "manual" ? "*" : "");
        }

        private readonly RhinoDoc _doc = RhinoDoc.ActiveDoc;
        private PrintSettings _s;
        private bool _loading;
        private int _portAtOpen;
        private readonly List<Action> _refresh = new List<Action>();
        private readonly Label _status = new Label { TextColor = Colors.Gray };
        private readonly Label _error = new Label { TextColor = Colors.Red };
        private readonly Panel _host = new Panel();
        private readonly Dictionary<string, Control> _pages = new Dictionary<string, Control>();
        private readonly ObservableCollection<SupportRow> _rows = new ObservableCollection<SupportRow>();
        private readonly Label _supportCount = new Label { TextColor = Colors.Gray };
        private readonly Label _supportNote = new Label { TextColor = Colors.Gray };
        private readonly StackLayout _tiltRows = new StackLayout { Spacing = 2 };
        private readonly List<Button> _busyButtons = new List<Button>();
        private GridView _grid;

        private static readonly string[][] Nav =
        {
            new[] { "general", "General" },
            new[] { "material", "Material" },
            new[] { "printing", "Printing" },
            new[] { "checks", "Checks" },
            new[] { "app", "Application" },
            new[] { "agent", "Agent" },
            new[] { "instructions", "Instructions" },
        };

        public SettingsDialog()
        {
            Title = "RhinoCC settings";
            Resizable = true;
            Size = new Size(860, 680);
            MinimumSize = new Size(700, 520);
            Padding = new Padding(10);
            _portAtOpen = AppSettings.Port;
            _s = _doc != null ? PrintOps.Settings(_doc) : new PrintSettings();

            var nav = new ListBox { Width = 150 };
            foreach (var n in Nav) nav.Items.Add(new ListItem { Key = n[0], Text = n[1] });
            nav.SelectedIndexChanged += (s, e) => { if (nav.SelectedKey != null) Show(nav.SelectedKey); };

            var done = new Button { Text = "Done" };
            done.Click += (s, e) => Close();
            DefaultButton = done;
            AbortButton = done;

            Content = new TableLayout
            {
                Spacing = new Size(10, 8),
                Rows =
                {
                    new TableRow(new TableCell(nav), new TableCell(_host, true)) { ScaleHeight = true },
                    new TableRow(new TableCell(_error), new TableCell(new TableLayout(new TableRow(new TableCell(_status, true), done)) { Spacing = new Size(6, 0) }, true)),
                },
            };

            PrintOps.Changed += OnModelChanged;
            Closed += (s, e) =>
            {
                PrintOps.Changed -= OnModelChanged;
                if (AppSettings.Port != _portAtOpen) ChatController.Instance.RestartServer();
            };
            nav.SelectedIndex = 0;
        }

        // ------------------------------------------------------------ pages

        private void Show(string key)
        {
            if (!_pages.TryGetValue(key, out var page))
            {
                switch (key)
                {
                    case "general": page = Model(General); break;
                    case "material": page = Model(Material); break;
                    case "printing": page = Model(Printing); break;
                    case "checks": page = Model(Checks); break;
                    case "app": page = App(); break;
                    case "agent": page = Agent(); break;
                    default: page = InstructionsPage(); break;
                }
                _pages[key] = page;
            }
            _host.Content = page;
            Reload();
        }

        /// <summary>A model page, or a hint when no model is open.</summary>
        private Control Model(Func<Control> build)
        {
            if (_doc == null) return Pad(new Label { Text = "Open a model to change its settings.", TextColor = Colors.Gray });
            return build();
        }

        private static Control Pad(Control c) => new Scrollable { Border = BorderType.None, ExpandContentWidth = true, Content = new Panel { Padding = new Padding(0, 0, 10, 0), Content = c } };

        private static StackLayout Page() => new StackLayout { Spacing = 6, HorizontalContentAlignment = HorizontalAlignment.Stretch };

        private static Control Section(string text, bool first = false) => new Label
        {
            Text = text.ToUpperInvariant(),
            TextColor = Colors.Gray,
            Font = Fonts.Sans(8.5f, FontStyle.Bold),
            // a gap above, except for the first one
            Height = first ? 20 : 34,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        private static Control Note(string text) => new Label { Text = text, TextColor = Colors.Gray, Wrap = WrapMode.Word };

        private static Control Field(string label, Control control, string hint = null)
        {
            var row = new TableLayout(new TableRow(new TableCell(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 190 }), new TableCell(control, true))) { Spacing = new Size(6, 0) };
            if (hint == null) return row;
            return new StackLayout { Spacing = 1, HorizontalContentAlignment = HorizontalAlignment.Stretch, Items = { row, new Label { Text = hint, TextColor = Colors.Gray, Wrap = WrapMode.Word, Font = Fonts.Sans(8.5f) } } };
        }

        private static Control Line(params Control[] items) => new StackLayout
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center,
        }.With(items);

        // ---- the model

        private Control General()
        {
            var notes = new TextArea { Wrap = true, Height = 140 };
            notes.TextChanged += (s, e) => { if (!_loading) Apply(x => x.Notes = notes.Text); };
            _refresh.Add(() => { if (notes.Text != _s.Notes) notes.Text = _s.Notes; });
            var p = Page();
            p.Items.Add(Section("This model", true));
            p.Items.Add(Note("These settings are stored in the model file. The agent reads and changes the same values."));
            p.Items.Add(Field("Notes", notes));
            return Pad(p);
        }

        private Control Material()
        {
            var metal = new DropDown();
            foreach (var m in Metals.Density.Keys) metal.Items.Add(new ListItem { Key = m, Text = $"{m} ({Metals.Density[m]:0.0##} g/cm³)" });
            metal.SelectedIndexChanged += (s, e) => { if (!_loading && metal.SelectedKey != null) Apply(x => x.Metal = metal.SelectedKey); };
            _refresh.Add(() => metal.SelectedKey = _s.Metal);
            var process = new DropDown();
            process.Items.Add(new ListItem { Key = "casting", Text = "Casting (resin master, lost-wax)" });
            process.Items.Add(new ListItem { Key = "direct", Text = "Direct (the resin is the piece)" });
            process.SelectedIndexChanged += (s, e) => { if (!_loading && process.SelectedKey != null) Apply(x => x.Process = process.SelectedKey); };
            _refresh.Add(() => process.SelectedKey = _s.Process);
            var p = Page();
            p.Items.Add(Section("Material", true));
            p.Items.Add(Field("Metal", metal, "Used for weights: `analyze` reports the weight of the piece in this metal, without sprue."));
            p.Items.Add(Field("Process", process));
            return Pad(p);
        }

        private NumericStepper Number(Func<PrintSettings, double> get, Action<PrintSettings, double> set, double min, double max, double step, int places = 2)
        {
            var n = new NumericStepper { MinValue = min, MaxValue = max, Increment = step, DecimalPlaces = places, MaximumDecimalPlaces = places, Width = 90 };
            n.ValueChanged += (s, e) => { if (!_loading) Apply(x => set(x, n.Value)); };
            _refresh.Add(() => n.Value = get(_s));
            return n;
        }

        private static Control Unit(Control c, string unit) => Line(c, new Label { Text = unit, TextColor = Colors.Gray, VerticalAlignment = VerticalAlignment.Center });

        private Control Printing()
        {
            var printer = new DropDown();
            printer.Items.Add(new ListItem { Key = "Elegoo Mars 5", Text = "Elegoo Mars 5" });
            printer.SelectedIndex = 0;
            var minWall = Number(x => x.MinWall, (x, v) => x.MinWall = v, 0.1, 5, 0.1);
            var tilt = Number(x => x.Tilt, (x, v) => x.Tilt = v, -90, 90, 5, 0);
            var suggest = Busy("Suggest tilt");
            suggest.Click += (s, e) => SuggestTilt();

            var enabled = new CheckBox { Text = "Use supports (pillars and raft are built as solids on the \"supports\" layer)" };
            enabled.CheckedChanged += (s, e) => { if (!_loading) Apply(x => x.Supports.Enabled = enabled.Checked == true); };
            _refresh.Add(() => enabled.Checked = _s.Supports.Enabled);

            var auto = Busy("Auto supports");
            auto.Click += (s, e) => AutoSupports();
            var clear = Busy("Clear auto");
            clear.Click += (s, e) => RemoveSupports(r => r.Source == "auto");
            var rebuild = Busy("Rebuild");
            rebuild.Click += (s, e) => Rebuild();

            _grid = new GridView { Height = 150, AllowMultipleSelection = true, DataStore = _rows, ShowHeader = true };
            _grid.Columns.Add(new GridColumn { HeaderText = "#", DataCell = new TextBoxCell { Binding = Binding.Delegate<SupportRow, string>(r => r.Label) }, Width = 50 });
            foreach (var axis in new[] { "X", "Y", "Z" })
            {
                var a = axis;
                _grid.Columns.Add(new GridColumn
                {
                    HeaderText = a,
                    Editable = true,
                    Width = 100,
                    DataCell = new TextBoxCell
                    {
                        Binding = Binding.Delegate<SupportRow, string>(
                            r => a == "X" ? r.X : a == "Y" ? r.Y : r.Z,
                            (r, v) => { if (a == "X") r.X = v; else if (a == "Y") r.Y = v; else r.Z = v; }),
                    },
                });
            }
            _grid.CellEdited += (s, e) => { if (e.Item is SupportRow row) EditSupport(row); };
            var add = Button("Add");
            add.Click += (s, e) => AddSupport();
            var remove = Button("Remove selected");
            remove.Click += (s, e) =>
            {
                var ids = _grid.SelectedItems.OfType<SupportRow>().Select(r => r.Id).ToList();
                RemoveSupports(r => ids.Contains(r.Id));
            };

            var sprue = new CheckBox { Text = "Put a casting sprue on the piece (a solid on the \"sprue\" layer)" };
            sprue.CheckedChanged += (s, e) => { if (!_loading) Apply(x => x.Sprue.Enabled = sprue.Checked == true); };
            _refresh.Add(() => sprue.Checked = _s.Sprue.Enabled);
            var at = new[] { 0, 1, 2 }.Select(i =>
            {
                var n = new NumericStepper { DecimalPlaces = 2, MaximumDecimalPlaces = 3, Increment = 0.5, Width = 90, MinValue = -1e9, MaxValue = 1e9 };
                n.ValueChanged += (s, e) =>
                {
                    if (_loading) return;
                    Apply(x =>
                    {
                        var cur = x.Sprue.At ?? CurrentSpruePoint();
                        cur[i] = n.Value;
                        x.Sprue.At = cur;
                    });
                };
                return n;
            }).ToArray();
            var mode = new DropDown();
            mode.Items.Add(new ListItem { Key = "auto", Text = "Automatic (thickest, least visible spot)" });
            mode.Items.Add(new ListItem { Key = "manual", Text = "By hand (closest surface point to the position)" });
            mode.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || mode.SelectedKey == null) return;
                if (mode.SelectedKey == "auto") Apply(x => x.Sprue.At = null);
                else Apply(x => x.Sprue.At = x.Sprue.At ?? CurrentSpruePoint());
            };
            _refresh.Add(() =>
            {
                var cur = _s.Sprue.At ?? CurrentSpruePoint();
                for (var i = 0; i < 3; i++) at[i].Value = cur[i];
                mode.SelectedKey = _s.Sprue.At != null ? "manual" : "auto";
                mode.Enabled = _s.Sprue.Enabled;
                foreach (var n in at) n.Enabled = _s.Sprue.Enabled;
            });

            var p = Page();
            p.Items.Add(Section("Printer", true));
            p.Items.Add(Field("Printer", printer));
            p.Items.Add(Field("Minimum wall", Unit(minWall, "mm"), "`analyze` flags walls thinner than this."));
            p.Items.Add(Field("Tilt", Line(Unit(tilt, "°"), suggest), "With supports on, the piece is really turned this much about X and set on the plate (z = 0). 30° is usual; fine detail must face up. 0 turns it back."));
            p.Items.Add(_tiltRows);

            p.Items.Add(Section("Supports"));
            p.Items.Add(enabled);
            p.Items.Add(Field("Tip radius", Unit(Number(x => x.Supports.TipRadius, (x, v) => x.Supports.TipRadius = v, 0.1, 1, 0.05), "mm")));
            p.Items.Add(Field("Base radius", Unit(Number(x => x.Supports.BaseRadius, (x, v) => x.Supports.BaseRadius = v, 0.2, 2, 0.1), "mm")));
            p.Items.Add(Field("Overhang angle", Unit(Number(x => x.Supports.OverhangAngle, (x, v) => x.Supports.OverhangAngle = v, 0, 80, 5, 0), "°"), "Faces pointing down within this many degrees of horizontal get supports."));
            p.Items.Add(Field("Spacing", Unit(Number(x => x.Supports.Spacing, (x, v) => x.Supports.Spacing = v, 1, 10, 0.5), "mm")));
            p.Items.Add(Field("Raft radius", Unit(Number(x => x.Supports.RaftRadius, (x, v) => x.Supports.RaftRadius = v, 0, 10, 0.5), "mm")));
            p.Items.Add(Field("Raft thickness", Unit(Number(x => x.Supports.RaftThickness, (x, v) => x.Supports.RaftThickness = v, 0, 9.9, 0.1), "mm"), "0 = no raft. Must stay below the lift."));
            p.Items.Add(Field("Lift", Unit(Number(x => x.Supports.Lift, (x, v) => x.Supports.Lift = v, 1, 10, 0.5), "mm"), "Gap between the lowest point of the piece and the plate."));
            p.Items.Add(Line(_supportCount, auto, clear, rebuild));
            p.Items.Add(_supportNote);
            p.Items.Add(Line(new Label { Text = "Support list", Font = Fonts.Sans(9f, FontStyle.Bold) }, add, remove));
            p.Items.Add(_grid);
            p.Items.Add(Note("* = manual (kept by Auto supports). The contacts are ordinary points on the \"support-points\" layer: move, copy or delete them in Rhino and press Rebuild."));

            p.Items.Add(Section("Sprue"));
            p.Items.Add(sprue);
            p.Items.Add(Field("Diameter", Unit(Number(x => x.Sprue.Diameter, (x, v) => x.Sprue.Diameter = v, 0.5, 6, 0.5), "mm")));
            p.Items.Add(Field("Length", Unit(Number(x => x.Sprue.Length, (x, v) => x.Sprue.Length = v, 1, 30, 0.5), "mm")));
            p.Items.Add(Field("Placement", mode));
            p.Items.Add(Field("Position", Line(at[0], at[1], at[2]), "Model units. Shows where the sprue is now; editing it switches to by hand."));
            return Pad(p);
        }

        private double[] CurrentSpruePoint()
        {
            var cur = _doc == null ? null : PrintOps.CurrentSprue(_doc);
            return cur.HasValue ? new[] { cur.Value.X, cur.Value.Y, cur.Value.Z } : new double[3];
        }

        private readonly TextArea _output = new TextArea { ReadOnly = true, Wrap = false, Font = Fonts.Monospace(9f), Height = 360 };

        private Control Checks()
        {
            var analyze = Busy("Analyze");
            analyze.Click += (s, e) =>
            {
                _output.Text = "Analyzing…";
                SetBusy(true);
                Task.Run(() => ChatController.Instance.RunCli("analyze")).ContinueWith(t => Application.Instance.AsyncInvoke(() =>
                {
                    _output.Text = t.IsFaulted ? "error: " + t.Exception.GetBaseException().Message : t.Result.Text;
                    SetBusy(false);
                }));
            };
            var p = Page();
            p.Items.Add(Section("Checks", true));
            p.Items.Add(Note("Checks the model's own solids against the settings: closed and valid, volume and weight in the metal, fit in the printer, thinnest walls, overhangs. Supports and sprue are left out."));
            p.Items.Add(Line(analyze));
            p.Items.Add(_output);
            return Pad(p);
        }

        // ---- the plugin

        private Control App()
        {
            var openAtStartup = new CheckBox { Text = "Open the chat panel when Rhino starts", Checked = AppSettings.OpenAtStartup };
            var autoUpdate = new CheckBox { Text = $"Update RhinoCC by itself from GitHub (this is version {UpdateService.Version})", Checked = AppSettings.AutoUpdate };
            var allTools = new CheckBox { Text = "Also let the agent use its own tools (shell, files, web)", Checked = AppSettings.AllTools };
            var chimeOn = new CheckBox { Text = "Play a chime when a reply takes long", Checked = AppSettings.ChimeOn };
            var chimeSecs = new NumericStepper { MinValue = 0, MaxValue = 3600, Value = AppSettings.ChimeSecs, Width = 80 };
            var chimePlay = new Button { Text = "Play" };
            chimePlay.Click += (s, e) => Chime.Play();
            chimeSecs.Enabled = chimeOn.Checked == true;
            void Save()
            {
                chimeSecs.Enabled = chimeOn.Checked == true;
                AppSettings.OpenAtStartup = openAtStartup.Checked == true;
                AppSettings.AutoUpdate = autoUpdate.Checked == true;
                AppSettings.AllTools = allTools.Checked == true;
                AppSettings.ChimeOn = chimeOn.Checked == true;
                AppSettings.ChimeSecs = (int)chimeSecs.Value;
            }
            foreach (var c in new[] { openAtStartup, autoUpdate, allTools, chimeOn }) c.CheckedChanged += (s, e) => Save();
            chimeSecs.ValueChanged += (s, e) => Save();
            var p = Page();
            p.Items.Add(Section("Application", true));
            p.Items.Add(openAtStartup);
            p.Items.Add(autoUpdate);
            p.Items.Add(Section("Chat"));
            p.Items.Add(allTools);
            p.Items.Add(Note("Off: the agent can only drive Rhino through the command line, nothing else on this computer."));
            p.Items.Add(chimeOn);
            p.Items.Add(Field("Chime after", Unit(chimeSecs, "seconds"), null));
            p.Items.Add(Line(chimePlay));
            return Pad(p);
        }

        private Control Agent()
        {
            var agent = new DropDown();
            agent.Items.AddRange(Engines.All.Select(en => new ListItem { Text = en.Label, Key = en.Id }));
            agent.SelectedKey = AppSettings.DefaultAgent;
            var model = new ComboBox { Text = AppSettings.DefaultModel };
            var effort = new DropDown();
            const string none = "(default)";
            var filling = false;
            void FillAgent()
            {
                filling = true;
                var engine = Engines.Get(agent.SelectedKey);
                var m = model.Text;
                model.Items.Clear();
                foreach (var mi in engine.Models) model.Items.Add(new ListItem { Text = mi.Id, Key = mi.Id });
                model.Text = m;
                var wanted = effort.SelectedKey ?? (AppSettings.DefaultEffort.Length > 0 ? AppSettings.DefaultEffort : none);
                effort.Items.Clear();
                effort.Items.Add(new ListItem { Text = none, Key = none });
                foreach (var f in engine.Efforts) effort.Items.Add(new ListItem { Text = f, Key = f });
                effort.SelectedKey = engine.Efforts.Contains(wanted) ? wanted : none;
                filling = false;
            }
            FillAgent();
            var claude = new TextBox { Text = AppSettings.ClaudePath };
            var codex = new TextBox { Text = AppSettings.CodexPath };
            var shell = new TextBox { Text = AppSettings.HostShell, Enabled = ProcessHost.IsWine };
            var port = new NumericStepper { MinValue = 1024, MaxValue = 65535, Value = AppSettings.Port, Width = 90 };
            void Save()
            {
                if (filling) return;
                AppSettings.DefaultAgent = agent.SelectedKey;
                AppSettings.DefaultModel = model.Text.Trim();
                AppSettings.DefaultEffort = effort.SelectedKey == null || effort.SelectedKey == none ? "" : effort.SelectedKey;
                AppSettings.ClaudePath = claude.Text.Trim().Length > 0 ? claude.Text.Trim() : "claude";
                AppSettings.CodexPath = codex.Text.Trim().Length > 0 ? codex.Text.Trim() : "codex";
                AppSettings.HostShell = shell.Text.Trim().Length > 0 ? shell.Text.Trim() : "/bin/sh";
                AppSettings.Port = (int)port.Value;
            }
            agent.SelectedIndexChanged += (s, e) => { FillAgent(); Save(); };
            effort.SelectedIndexChanged += (s, e) => Save();
            model.TextChanged += (s, e) => Save();
            foreach (var t in new[] { claude, codex, shell }) t.TextChanged += (s, e) => Save();
            port.ValueChanged += (s, e) => Save();

            var check = new Button { Text = "Check agents" };
            var checkResult = new Label { TextColor = Colors.Gray };
            check.Click += (s, e) =>
            {
                checkResult.Text = "Checking…";
                var settings = new AgentSettings { ClaudePath = claude.Text.Trim(), CodexPath = codex.Text.Trim(), HostShell = shell.Text.Trim() };
                Task.Run(() =>
                {
                    var c = ProcessHost.Probe("claude", settings);
                    var x = ProcessHost.Probe("codex", settings);
                    Application.Instance.AsyncInvoke(() =>
                        checkResult.Text = $"Claude: {(c.Key ? "found, " : "NOT FOUND, ")}{c.Value}\nCodex: {(x.Key ? "found, " : "NOT FOUND, ")}{x.Value}");
                });
            };

            var where = ProcessHost.IsWine ? "Running under Wine: the CLIs are started on the Linux host through the shell below." : "Running on native Windows / macOS.";
            var p = Page();
            p.Items.Add(Section("Agent for new conversations", true));
            p.Items.Add(Field("Agent", agent));
            p.Items.Add(Field("Model", model, "Empty = the agent's own."));
            p.Items.Add(Field("Thinking level", effort));
            p.Items.Add(Section("Agent programs"));
            p.Items.Add(Note(where));
            p.Items.Add(Field("Claude executable", claude));
            p.Items.Add(Field("Codex executable", codex));
            p.Items.Add(Field("Host shell (Wine only)", shell));
            p.Items.Add(Line(check));
            p.Items.Add(checkResult);
            p.Items.Add(Section("Connection"));
            p.Items.Add(Field("Server port (loopback)", port, "The agent reaches Rhino through a small server on this computer only. A new port applies when this window closes."));
            return Pad(p);
        }

        private Control InstructionsPage()
        {
            var text = new TextArea { Text = AppSettings.Instructions, Wrap = true };
            text.TextChanged += (s, e) => AppSettings.Instructions = text.Text;
            var reset = new Button { Text = "Reset to the default" };
            reset.Click += (s, e) => text.Text = AppSettings.DefaultInstructions;
            return new TableLayout
            {
                Spacing = new Size(6, 6),
                Rows =
                {
                    new TableRow(Section("Instructions", true)),
                    new TableRow(Note("Your instructions for the agent. They are sent before the command reference, which the plugin writes by itself from the commands.")),
                    new TableRow(text) { ScaleHeight = true },
                    new TableRow(new TableLayout(new TableRow(reset, new TableCell(null, true))) { Spacing = new Size(6, 0) }),
                },
            };
        }

        // ------------------------------------------------------------ the model's settings

        private Button Button(string text) => new Button { Text = text };

        /// <summary>A button that is disabled while a calculation runs.</summary>
        private Button Busy(string text)
        {
            var b = new Button { Text = text };
            _busyButtons.Add(b);
            return b;
        }

        private void SetBusy(bool on)
        {
            foreach (var b in _busyButtons) b.Enabled = !on;
        }

        private void OnModelChanged() => Application.Instance.AsyncInvoke(Reload);

        /// <summary>Shows the stored settings and supports in the controls (the model may have been changed by the agent).</summary>
        private void Reload()
        {
            if (_doc == null) return;
            _s = PrintOps.Settings(_doc);
            _loading = true;
            try
            {
                foreach (var r in _refresh) r();
                RefreshSupports();
            }
            finally { _loading = false; }
        }

        private void RefreshSupports()
        {
            var toMm = PrintOps.ToMm(_doc);
            var list = PrintOps.ReadSupports(_doc);
            _rows.Clear();
            foreach (var x in list)
                _rows.Add(new SupportRow { Id = x.Id, Source = x.Source, X = Fmt.N(x.P.X / toMm), Y = Fmt.N(x.P.Y / toMm), Z = Fmt.N(x.P.Z / toMm) });
            _supportCount.Text = $"{list.Count} support(s): {list.Count(x => x.Source == "auto")} auto, {list.Count(x => x.Source == "manual")} manual";
        }

        /// <summary>Applies a change to the model's settings if the result is valid; otherwise says why and keeps the old ones.</summary>
        private void Apply(Action<PrintSettings> change)
        {
            if (_doc == null) return;
            var before = _s;
            var next = before.Clone();
            change(next);
            var problem = next.Validate();
            _error.Text = problem ?? "";
            if (problem != null) return;
            PrintOps.SaveSettings(_doc, next); // raises Changed: the controls reload from the document
            _status.Text = PrintOps.Follow(_doc, before, next);
            _s = next;
            Reload();
        }

        private void Fail(string text) => _supportNote.Text = text;

        /// <summary>Gathers the piece on the UI thread, works on it elsewhere, applies the result back here.</summary>
        private void Compute<T>(Func<Piece, T> work, Action<Piece, T> done)
        {
            Piece piece;
            try { piece = PrintOps.Gather(_doc, PrintOps.DefaultTargets(_doc)); }
            catch (CliException e) { Fail(e.Message); return; }
            SetBusy(true);
            Fail("Working…");
            Task.Run(() => work(piece)).ContinueWith(t => Application.Instance.AsyncInvoke(() =>
            {
                SetBusy(false);
                if (t.IsFaulted) { Fail("failed: " + t.Exception.GetBaseException().Message); return; }
                try { Fail(""); done(piece, t.Result); }
                catch (Exception e) { Fail("failed: " + e.Message); }
            }));
        }

        private void SuggestTilt()
        {
            var s = _s;
            Compute(p => Supports.RankTilts(p.Canonical(), -60, 60, 5, s.Supports.OverhangAngle, s.Supports.Lift), (piece, rows) =>
            {
                _tiltRows.Items.Clear();
                foreach (var r in rows.Take(5))
                {
                    var tilt = r.Tilt;
                    var use = new Button { Text = "Use", Enabled = Math.Abs(PrintOps.AppliedTilt(_doc) - tilt) > 1e-9 };
                    use.Click += (a, b) => { Apply(x => x.Tilt = tilt); _tiltRows.Items.Clear(); };
                    _tiltRows.Items.Add(Line(
                        new Label { Text = $"{r.Tilt:0}°", Width = 50, Font = Fonts.Sans(9f, _tiltRows.Items.Count == 0 ? FontStyle.Bold : FontStyle.None) },
                        new Label { Text = $"overhang {r.Overhang:0.#} mm²   flat on plate {r.Flat:0.#} mm²   {(r.Fits ? $"{r.Size.X:0} × {r.Size.Y:0} × {r.Size.Z:0} mm" : "does not fit")}", TextColor = Colors.Gray },
                        use));
                }
            });
        }

        private void AutoSupports()
        {
            if (!_s.Supports.Enabled) { Fail("Turn supports on first."); return; }
            var s = _s;
            var manual = PrintOps.ReadSupports(_doc).Where(x => x.Source == "manual").ToList();
            var next = PrintOps.NextId(PrintOps.ReadSupports(_doc));
            Compute(p => Core.Printing.Supports.Auto(p.Mesh, PrintOps.Params(s), new AutoParams { Angle = s.Supports.OverhangAngle, Spacing = s.Supports.Spacing }, manual, next), (piece, r) =>
            {
                if (r == null) { Fail("The piece has no geometry to support."); return; }
                var rec = _doc.BeginUndoRecord("RhinoCC supports");
                try
                {
                    PrintOps.WriteSupports(_doc, r.Supports);
                    PrintOps.BuildSupports(_doc, piece, s);
                }
                finally { _doc.EndUndoRecord(rec); }
                _doc.Views.Redraw();
                Reload();
                Fail($"{r.Stats.Added} added, {r.Stats.Blocked} spots skipped because the piece is in the way below them.");
            });
        }

        private void RemoveSupports(Func<SupportRow, bool> doomed)
        {
            var toRemove = _rows.Where(doomed).Select(r => r.Id).ToList();
            if (toRemove.Count == 0) return;
            var rest = PrintOps.ReadSupports(_doc).Where(x => !toRemove.Contains(x.Id)).ToList();
            ChangeSupports(rest);
        }

        private void AddSupport()
        {
            var list = PrintOps.ReadSupports(_doc);
            list.Add(new Support { Id = PrintOps.NextId(list), P = new Core.Geometry.Vec3(), Source = "manual" });
            ChangeSupports(list);
        }

        private void EditSupport(SupportRow row)
        {
            var toMm = PrintOps.ToMm(_doc);
            double X(string t) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            var x = X(row.X); var y = X(row.Y); var z = X(row.Z);
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) { Fail("Coordinates must be numbers."); Reload(); return; }
            var list = PrintOps.ReadSupports(_doc);
            var s = list.FirstOrDefault(i => i.Id == row.Id);
            if (s == null) return;
            s.P = new Core.Geometry.Vec3(x * toMm, y * toMm, z * toMm);
            s.Source = "manual"; // edited by hand
            ChangeSupports(list);
        }

        private void ChangeSupports(List<Support> list)
        {
            var rec = _doc.BeginUndoRecord("RhinoCC supports");
            try
            {
                PrintOps.WriteSupports(_doc, list);
                if (_s.Supports.Enabled)
                {
                    try { PrintOps.BuildSupports(_doc, PrintOps.Gather(_doc, PrintOps.DefaultTargets(_doc)), _s); }
                    catch (CliException e) { Fail(e.Message); }
                }
            }
            finally { _doc.EndUndoRecord(rec); }
            _doc.Views.Redraw();
            Reload();
        }

        private void Rebuild()
        {
            if (!_s.Supports.Enabled) { Fail("Turn supports on first."); return; }
            ChangeSupports(PrintOps.ReadSupports(_doc));
        }
    }

    internal static class LayoutExtensions
    {
        public static StackLayout With(this StackLayout stack, params Control[] items)
        {
            foreach (var i in items) stack.Items.Add(new StackLayoutItem(i));
            return stack;
        }
    }
}
