using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using RhinoCC.Core.Agents;
using RhinoCC.Core.Conversations;

namespace RhinoCC.Chat
{
    [Guid("6f1d3a42-8d0e-4c6b-9a57-2b3f0e5c7a11")]
    public class ChatPanel : Panel
    {
        private const string DefaultItem = "(default)";

        private readonly ChatController _chat = ChatController.Instance;
        private readonly DropDown _conversations = new DropDown();
        private readonly DropDown _agent = new DropDown();
        private readonly ComboBox _model = new ComboBox();
        private readonly DropDown _effort = new DropDown();
        private readonly TextArea _transcript = new TextArea { ReadOnly = true, Wrap = true, Font = Fonts.Monospace(9) };
        private readonly ImageView _picture = new ImageView { Visible = false, Height = 220 };
        private readonly TextArea _input = new TextArea { Wrap = true, Height = 70 };
        private readonly Button _send = new Button { Text = "Send" };
        private readonly Label _status = new Label { TextColor = Colors.Gray };
        private readonly UITimer _timer = new UITimer { Interval = 0.15 };
        private bool _dirty = true;
        private bool _updating;
        private string _shownImage;

        public ChatPanel()
        {
            var newButton = new Button { Text = "New" };
            var deleteButton = new Button { Text = "Delete" };
            var settingsButton = new Button { Text = "Settings…" };
            newButton.Click += (s, e) => _chat.New();
            deleteButton.Click += (s, e) =>
            {
                if (MessageBox.Show("Delete this conversation from the model?", "RhinoCC", MessageBoxButtons.YesNo, MessageBoxType.Question) == DialogResult.Yes)
                    _chat.DeleteCurrent();
            };
            settingsButton.Click += (s, e) => new SettingsDialog().ShowModal(this);

            _agent.Items.AddRange(Engines.All.Select(en => new ListItem { Text = en.Label, Key = en.Id }));
            _agent.SelectedIndexChanged += (s, e) => { if (!_updating) { FillEfforts(); Configure(); } };
            _effort.SelectedIndexChanged += (s, e) => { if (!_updating) Configure(); };
            _model.TextChanged += (s, e) => { if (!_updating) Configure(); };
            _conversations.SelectedIndexChanged += (s, e) =>
            {
                if (!_updating && _conversations.SelectedKey != null) _chat.Select(_conversations.SelectedKey);
            };

            _send.Click += (s, e) => SendOrStop();
            _input.KeyDown += (s, e) =>
            {
                if (e.Key == Keys.Enter && e.Control)
                {
                    e.Handled = true;
                    SendOrStop();
                }
            };

            Content = new TableLayout
            {
                Padding = new Padding(6),
                Spacing = new Size(4, 4),
                Rows =
                {
                    new TableRow(new TableLayout(new TableRow(new TableCell(_conversations, true), newButton, deleteButton, settingsButton) { }) { Spacing = new Size(4, 0) }),
                    new TableRow(new TableLayout(new TableRow(new Label { Text = "Agent", VerticalAlignment = VerticalAlignment.Center }, _agent,
                        new Label { Text = "Model", VerticalAlignment = VerticalAlignment.Center }, new TableCell(_model, true),
                        new Label { Text = "Effort", VerticalAlignment = VerticalAlignment.Center }, _effort)) { Spacing = new Size(4, 0) }),
                    new TableRow(_transcript) { ScaleHeight = true },
                    new TableRow(_picture),
                    new TableRow(_input),
                    new TableRow(new TableLayout(new TableRow(new TableCell(_status, true), _send)) { Spacing = new Size(4, 0) }),
                },
            };

            _chat.Changed += OnChanged;
            _timer.Elapsed += (s, e) => { if (_dirty) Refresh(); };
            Load += (s, e) => { _chat.Sync(); _timer.Start(); _dirty = true; };
            UnLoad += (s, e) => { _timer.Stop(); _chat.Changed -= OnChanged; };
            Rhino.RhinoDoc.EndOpenDocument += OnDocChanged;
            Rhino.RhinoDoc.CloseDocument += OnDocChanged;
        }

        private void OnDocChanged(object sender, EventArgs e) => Application.Instance.AsyncInvoke(() => { _chat.Sync(); _dirty = true; });

        private void OnChanged() => _dirty = true;

        private void SendOrStop()
        {
            if (_chat.Running)
            {
                _chat.Stop();
                return;
            }
            var text = _input.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            _input.Text = "";
            _chat.Send(text);
        }

        private void Configure()
        {
            _chat.Configure(_agent.SelectedKey, _model.Text, _effort.SelectedKey == DefaultItem ? null : _effort.SelectedKey);
            _dirty = true;
        }

        private void FillEfforts()
        {
            var engine = Engines.Get(_agent.SelectedKey);
            var wanted = _effort.SelectedKey;
            _effort.Items.Clear();
            _effort.Items.Add(new ListItem { Text = DefaultItem, Key = DefaultItem });
            foreach (var e in engine.Efforts) _effort.Items.Add(new ListItem { Text = e, Key = e });
            _effort.SelectedKey = engine.Efforts.Contains(wanted) ? wanted : DefaultItem;
            _model.DataStore = engine.Models.Select(m => m.Id).ToList();
        }

        private void Refresh()
        {
            _dirty = false;
            var c = _chat.Current;
            if (c == null) return;
            _updating = true;
            try
            {
                // the conversation list
                var wantedKeys = _chat.Conversations.Select(i => i.Id).ToList();
                if (!wantedKeys.Contains(c.Id)) wantedKeys.Insert(0, c.Id);
                if (!_conversations.Items.Select(i => i.Key).SequenceEqual(wantedKeys) || _conversations.Items.Any(i => i.Key == c.Id && i.Text != c.Title))
                {
                    _conversations.Items.Clear();
                    foreach (var key in wantedKeys)
                    {
                        var info = _chat.Conversations.FirstOrDefault(i => i.Id == key);
                        _conversations.Items.Add(new ListItem { Key = key, Text = info?.Title ?? c.Title });
                    }
                }
                _conversations.SelectedKey = c.Id;

                // this conversation's agent settings
                if (_agent.SelectedKey != c.Agent)
                {
                    _agent.SelectedKey = c.Agent;
                    FillEfforts();
                }
                else if (_effort.Items.Count == 0) FillEfforts();
                _effort.SelectedKey = c.Effort ?? DefaultItem;
                if (_model.Text != (c.Model ?? "")) _model.Text = c.Model ?? "";
                var idle = !_chat.Running;
                _agent.Enabled = _effort.Enabled = _model.Enabled = _conversations.Enabled = idle;

                var text = TranscriptRenderer.Render(c, Engines.Get(c.Agent).Label, _chat.Running);
                if (_transcript.Text != text)
                {
                    _transcript.Text = text;
                    try { _transcript.Selection = new Range<int>(text.Length, text.Length - 1); } catch { /* scrolling is a nicety */ }
                }
                _send.Text = _chat.Running ? "Stop" : "Send";
                _status.Text = _chat.Notice ?? (_chat.Running ? "Working…  (the agent drives Rhino through the `rhino` command line)" : "Ctrl+Enter to send");
                _status.TextColor = _chat.Notice != null ? Colors.OrangeRed : Colors.Gray;

                if (_shownImage != _chat.LatestImage)
                {
                    _shownImage = _chat.LatestImage;
                    _picture.Visible = _shownImage != null;
                    if (_shownImage != null)
                    {
                        try { _picture.Image = new Bitmap(new MemoryStream(Convert.FromBase64String(_shownImage))); }
                        catch { _picture.Visible = false; }
                    }
                }
            }
            finally
            {
                _updating = false;
            }
        }
    }
}
