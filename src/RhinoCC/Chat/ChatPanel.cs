using System;
using System.Collections.Generic;
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
        private readonly RichTextArea _transcript = new RichTextArea { ReadOnly = true, Wrap = true };
        private readonly TextArea _input = new TextArea { Wrap = true, Height = 70 };
        private readonly Button _send = new Button { Text = "Send" };
        private readonly ProgressBar _busy = new ProgressBar { Indeterminate = true, Visible = false, Height = 6 };
        private readonly Label _status = new Label { TextColor = Colors.Gray };
        private readonly Label _attached = new Label { TextColor = Colors.Gray, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _clear = new Button { Text = "Clear", Visible = false };
        private readonly List<ImageAttachment> _pending = new List<ImageAttachment>();
        private const int MaxImageBytes = 5 * 1024 * 1024;
        private readonly UITimer _timer = new UITimer { Interval = 0.15 };
        private bool _dirty = true;
        private string _shownRtf;
        private bool _updating;

        public ChatPanel()
        {
            // a docked panel otherwise opens at a sliver
            MinimumSize = new Size(380, 520);
            Size = new Size(460, 700);
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

            var attachButton = new Button { Text = "Attach…" };
            attachButton.Click += (s, e) => PickImages();
            _clear.Click += (s, e) => { _pending.Clear(); ShowPending(); };

            _send.Click += (s, e) => SendOrStop();
            _input.KeyDown += (s, e) =>
            {
                if (e.Key == Keys.Enter && e.Modifiers == Keys.None)
                {
                    e.Handled = true;
                    SendOrStop();
                }
                else if (e.Key == Keys.V && e.Control && PasteImage()) e.Handled = true;
            };
            // dropping picture files on the box
            _input.AllowDrop = true;
            _input.DragEnter += (s, e) => { if (e.Data.ContainsUris) e.Effects = DragEffects.Copy; };
            _input.DragDrop += (s, e) =>
            {
                if (!e.Data.ContainsUris) return;
                foreach (var uri in e.Data.Uris.Where(u => u.IsFile)) AddFile(uri.LocalPath);
                e.Effects = DragEffects.Copy;
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
                    new TableRow(_busy),
                    new TableRow(_input),
                    new TableRow(new TableLayout(new TableRow(attachButton, new TableCell(_attached, true), _clear)) { Spacing = new Size(4, 0) }),
                    new TableRow(new TableLayout(new TableRow(new TableCell(_status, true), _send)) { Spacing = new Size(4, 0) }),
                },
            };

            _chat.Changed += OnChanged;
            _timer.Elapsed += (s, e) =>
            {
                try
                {
                    // follows the active document, however it became active (open, tab switch, close)
                    _chat.Sync();
                    if (_dirty) Refresh();
                    else if (_chat.Running) ShowStatus();
                }
                catch (Exception ex) { RhinoCC.Core.Log.Write("panel refresh failed: " + ex); _status.Text = "Panel error: " + ex.Message; _status.TextColor = Colors.OrangeRed; }
            };
            // Rhino hosts the panel without necessarily raising Load, so the timer cannot wait for it
            _timer.Start();
            RhinoCC.Core.Log.Write("chat panel created");
            Rhino.RhinoDoc.EndOpenDocument += (s, e) =>
            {
                var doc = e.Document;
                if (!e.Merge) Application.Instance.AsyncInvoke(() => { _chat.Reload(doc); _dirty = true; });
            };
        }

        private void OnChanged() => _dirty = true;

        private void SendOrStop()
        {
            if (_chat.Running)
            {
                _chat.Stop();
                return;
            }
            var text = _input.Text;
            if (string.IsNullOrWhiteSpace(text) && _pending.Count == 0) return;
            _input.Text = "";
            var images = _pending.ToList();
            _pending.Clear();
            ShowPending();
            _chat.Send(text, images);
        }

        private void ShowPending()
        {
            _attached.Text = _pending.Count == 0 ? "" : "Attached: " + string.Join(", ", _pending.Select(p => p.Name));
            _clear.Visible = _pending.Count > 0;
        }

        private void Add(string name, string mime, byte[] bytes)
        {
            if (bytes.Length > MaxImageBytes)
            {
                _attached.Text = $"{name} is larger than 5 MB; use a smaller picture.";
                return;
            }
            _pending.Add(new ImageAttachment { Name = name, Mime = mime, Base64 = Convert.ToBase64String(bytes) });
            ShowPending();
        }

        private void AddFile(string path)
        {
            var mime = MimeOf(path);
            if (mime == null)
            {
                _attached.Text = "Only png, jpg, gif and webp pictures can be attached.";
                return;
            }
            try { Add(Path.GetFileName(path), mime, File.ReadAllBytes(path)); }
            catch (Exception e) { _attached.Text = "Could not read the picture: " + e.Message; }
        }

        private static string MimeOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return null;
            }
        }

        private void PickImages()
        {
            var dialog = new OpenFileDialog { Title = "Attach pictures", MultiSelect = true };
            dialog.Filters.Add(new FileFilter("Pictures", ".png", ".jpg", ".jpeg", ".gif", ".webp"));
            if (dialog.ShowDialog(this) != DialogResult.Ok) return;
            foreach (var f in dialog.Filenames) AddFile(f);
        }

        /// <summary>Ctrl+V with a picture on the clipboard attaches it; with anything else the box pastes as usual.</summary>
        private bool PasteImage()
        {
            try
            {
                var clipboard = Clipboard.Instance;
                if (!clipboard.ContainsImage) return false;
                using (var bmp = clipboard.Image as Bitmap)
                {
                    if (bmp == null) return false;
                    Add($"pasted-{_pending.Count + 1}.png", "image/png", bmp.ToByteArray(ImageFormat.Png));
                    return true;
                }
            }
            catch
            {
                return false;
            }
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
            var model = _model.Text;
            _model.Items.Clear();
            foreach (var m in engine.Models) _model.Items.Add(new ListItem { Text = m.Id, Key = m.Id });
            _model.Text = model;
            RhinoCC.Core.Log.Write($"model list for {engine.Id}: {_model.Items.Count} item(s), text '{_model.Text}'");
        }

        private void ShowStatus()
        {
            _busy.Visible = _chat.Running;
            if (_chat.Running)
            {
                var secs = (int)(DateTime.UtcNow - _chat.RunStarted).TotalSeconds;
                _status.Text = $"{Engines.Get(_chat.Current.Agent).Label} is working… {secs} s  (Stop to cancel)";
            }
            else _status.Text = _chat.Notice ?? "Enter to send, Shift+Enter for a new line";
            _status.TextColor = _chat.Notice != null ? Colors.OrangeRed : Colors.Gray;
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
                else if (_effort.Items.Count == 0 || _model.Items.Count != Engines.Get(c.Agent).Models.Count) FillEfforts();
                _effort.SelectedKey = c.Effort ?? DefaultItem;
                if (_model.Text != (c.Model ?? "")) _model.Text = c.Model ?? "";
                var idle = !_chat.Running;
                _agent.Enabled = _effort.Enabled = _model.Enabled = _conversations.Enabled = idle;

                var rtf = RtfRenderer.Render(c, Engines.Get(c.Agent).Label, _chat.Running);
                if (_shownRtf != rtf)
                {
                    _shownRtf = rtf;
                    _transcript.Rtf = rtf;
                    // setting the text puts the view back at the top; Append with scrollToCursor moves the caret to the end and scrolls there
                    try { _transcript.Append("", true); } catch { /* scrolling is a nicety */ }
                }
                _send.Text = _chat.Running ? "Stop" : "Send";
                ShowStatus();
            }
            finally
            {
                _updating = false;
            }
        }
    }
}
