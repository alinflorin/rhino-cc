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
            MinimumSize = new Size(560, 520);
            Padding = new Padding(10);

            var agent = new DropDown();
            agent.Items.AddRange(Engines.All.Select(en => new ListItem { Text = en.Label, Key = en.Id }));
            agent.SelectedKey = AppSettings.DefaultAgent;
            var claude = new TextBox { Text = AppSettings.ClaudePath };
            var codex = new TextBox { Text = AppSettings.CodexPath };
            var shell = new TextBox { Text = AppSettings.HostShell, Enabled = ProcessHost.IsWine };
            var port = new NumericStepper { MinValue = 1024, MaxValue = 65535, Value = AppSettings.Port };
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
                AppSettings.ClaudePath = claude.Text.Trim().Length > 0 ? claude.Text.Trim() : "claude";
                AppSettings.CodexPath = codex.Text.Trim().Length > 0 ? codex.Text.Trim() : "codex";
                AppSettings.HostShell = shell.Text.Trim().Length > 0 ? shell.Text.Trim() : "/bin/sh";
                AppSettings.Port = (int)port.Value;
                AppSettings.AllTools = allTools.Checked == true;
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
                    new TableRow(Row("Claude executable", claude)),
                    new TableRow(Row("Codex executable", codex)),
                    new TableRow(Row("Host shell (Wine only)", shell)),
                    new TableRow(Row("Agent server port (loopback)", port)),
                    new TableRow(allTools),
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
