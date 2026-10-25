using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using RhinoCC.Core.Agents;

namespace RhinoCC.Chat
{
    internal sealed class SettingsDialog : Dialog
    {
        public SettingsDialog()
        {
            Title = "RhinoCC settings";
            MinimumSize = new Size(580, 600);
            Padding = new Padding(10);

            var agent = new DropDown();
            agent.Items.AddRange(Engines.All.Select(en => new ListItem { Text = en.Label, Key = en.Id }));
            agent.SelectedKey = AppSettings.DefaultAgent;
            var model = new ComboBox { Text = AppSettings.DefaultModel };
            var effort = new DropDown();
            const string none = "(default)";
            void FillAgent()
            {
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
            }
            agent.SelectedIndexChanged += (s, e) => FillAgent();
            FillAgent();
            var claude = new TextBox { Text = AppSettings.ClaudePath };
            var codex = new TextBox { Text = AppSettings.CodexPath };
            var shell = new TextBox { Text = AppSettings.HostShell, Enabled = ProcessHost.IsWine };
            var port = new NumericStepper { MinValue = 1024, MaxValue = 65535, Value = AppSettings.Port };
            var openAtStartup = new CheckBox { Text = "Open the chat panel when Rhino starts", Checked = AppSettings.OpenAtStartup };
            var chimeOn = new CheckBox { Text = "Play a chime when a reply takes long", Checked = AppSettings.ChimeOn };
            var chimeSecs = new NumericStepper { MinValue = 0, MaxValue = 3600, Value = AppSettings.ChimeSecs, Width = 70 };
            var chimePlay = new Button { Text = "Play" };
            chimePlay.Click += (s, e) => Chime.Play();
            chimeOn.CheckedChanged += (s, e) => chimeSecs.Enabled = chimeOn.Checked == true;
            chimeSecs.Enabled = chimeOn.Checked == true;
            var check = new Button { Text = "Check agents" };
            var checkResult = new Label { TextColor = Colors.Gray, Text = "" };
            check.Click += (s, e) =>
            {
                checkResult.Text = "Checking…";
                var settings = new AgentSettings { ClaudePath = claude.Text.Trim(), CodexPath = codex.Text.Trim(), HostShell = shell.Text.Trim() };
                System.Threading.Tasks.Task.Run(() =>
                {
                    var c = ProcessHost.Probe("claude", settings);
                    var x = ProcessHost.Probe("codex", settings);
                    Application.Instance.AsyncInvoke(() =>
                        checkResult.Text = $"Claude: {(c.Key ? "found, " : "NOT FOUND, ")}{c.Value}\nCodex: {(x.Key ? "found, " : "NOT FOUND, ")}{x.Value}");
                });
            };
            var allTools = new CheckBox { Text = "Also let the agent use its own tools (shell, files, web)", Checked = AppSettings.AllTools };
            var instructions = new TextArea { Text = AppSettings.Instructions, Wrap = true, Height = 170 };

            var reset = new Button { Text = "Reset" };
            reset.Click += (s, e) => instructions.Text = AppSettings.DefaultInstructions;
            var ok = new Button { Text = "OK" };
            var cancel = new Button { Text = "Cancel" };
            ok.Click += (s, e) =>
            {
                var restart = (int)port.Value != AppSettings.Port;
                AppSettings.DefaultAgent = agent.SelectedKey;
                AppSettings.DefaultModel = model.Text.Trim();
                AppSettings.DefaultEffort = effort.SelectedKey == none ? "" : effort.SelectedKey;
                AppSettings.ClaudePath = claude.Text.Trim().Length > 0 ? claude.Text.Trim() : "claude";
                AppSettings.CodexPath = codex.Text.Trim().Length > 0 ? codex.Text.Trim() : "codex";
                AppSettings.HostShell = shell.Text.Trim().Length > 0 ? shell.Text.Trim() : "/bin/sh";
                AppSettings.Port = (int)port.Value;
                AppSettings.ChimeOn = chimeOn.Checked == true;
                AppSettings.ChimeSecs = (int)chimeSecs.Value;
                AppSettings.AllTools = allTools.Checked == true;
                AppSettings.OpenAtStartup = openAtStartup.Checked == true;
                AppSettings.Instructions = instructions.Text;
                if (restart) ChatController.Instance.RestartServer();
                Close();
            };
            cancel.Click += (s, e) => Close();
            DefaultButton = ok;
            AbortButton = cancel;

            var where = ProcessHost.IsWine ? "Running under Wine: the CLIs are started on the Linux host through the shell below." : "Running on native Windows / macOS.";
            Content = new TableLayout
            {
                Spacing = new Size(6, 6),
                Rows =
                {
                    new TableRow(new Label { Text = where, TextColor = Colors.Gray }),
                    new TableRow(Row("Default agent for new conversations", agent)),
                    new TableRow(Row("Default model (empty = agent's own)", model)),
                    new TableRow(Row("Default thinking level", effort)),
                    new TableRow(Row("Claude executable", claude)),
                    new TableRow(Row("Codex executable", codex)),
                    new TableRow(Row("Host shell (Wine only)", shell)),
                    new TableRow(Row("Agent server port (loopback)", port)),
                    new TableRow(new TableLayout(new TableRow(check, new TableCell(checkResult, true))) { Spacing = new Size(6, 0) }),
                    new TableRow(new TableLayout(new TableRow(chimeOn, new Label { Text = "after (seconds)", VerticalAlignment = VerticalAlignment.Center }, chimeSecs, chimePlay)) { Spacing = new Size(6, 0) }),
                    new TableRow(allTools),
                    new TableRow(openAtStartup),
                    new TableRow(new Label { Text = "Your instructions for the agent (sent before the command reference):" }),
                    new TableRow(instructions) { ScaleHeight = true },
                    new TableRow(new TableLayout(new TableRow(reset, new TableCell(null, true), cancel, ok)) { Spacing = new Size(6, 0) }),
                },
            };
        }

        private static Control Row(string label, Control control) =>
            new TableLayout(new TableRow(new TableCell(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center }) { ScaleWidth = false }, new TableCell(control, true))) { Spacing = new Size(6, 0) };
    }
}
