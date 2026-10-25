using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Eto.Forms;
using Rhino;
using RhinoCC.Cli;
using RhinoCC.Core.Agents;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Conversations;
using RhinoCC.Core.Mcp;

namespace RhinoCC.Chat
{
    /// <summary>
    /// Owns the chat: the conversations of the open document, the running agent and the MCP server. The panel is only
    /// a view of this, so a turn keeps running when the panel is closed.
    /// </summary>
    internal sealed class ChatController
    {
        public static ChatController Instance { get; } = new ChatController();

        private readonly RhinoCli _cli;
        private readonly McpServer _server;
        private RhinoDoc _doc;
        private Conversation _live;
        private ChatMessage _liveMessage;
        private AgentRun _run;

        public Conversation Current { get; private set; }
        public List<ConversationInfo> Conversations { get; private set; } = new List<ConversationInfo>();
        public bool Running { get; private set; }
        /// <summary>The last picture a tool returned (base64 PNG), shown under the transcript.</summary>
        public string LatestImage { get; private set; }
        public string Notice { get; private set; }

        /// <summary>Raised on the UI thread whenever what the panel shows has changed.</summary>
        public event Action Changed;

        private ChatController()
        {
            _cli = RhinoCommands.Build();
            _server = new McpServer(_cli, BuildInstructions);
        }

        public string McpUrl => _server.IsRunning ? _server.Url(false) : null;

        public string BuildInstructions()
        {
            var sb = new StringBuilder();
            var custom = AppSettings.Instructions.Trim();
            if (custom.Length > 0) sb.Append(custom).Append("\n\n");
            sb.Append(_cli.Reference(false)); // one line per command; `help COMMAND` has the options, and keeps the prompt short
            return sb.ToString();
        }

        public bool EnsureServer()
        {
            if (_server.IsRunning) return true;
            try
            {
                _server.Start(AppSettings.Port);
                return true;
            }
            catch (Exception e)
            {
                Notice = $"Could not start the agent server on port {AppSettings.Port} ({e.Message}). Pick another port in the settings.";
                return false;
            }
        }

        public void RestartServer()
        {
            _server.Stop();
        }

        // ------------------------------------------------------------ conversations of the open document

        /// <summary>Follows the active document: loads its conversations when it is not the one shown.</summary>
        public void Sync()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || (_doc != null && _doc.RuntimeSerialNumber == doc.RuntimeSerialNumber && Current != null)) return;
            if (Running) return;
            _doc = doc;
            var store = new ConversationStore(new DocStringStore(doc));
            Conversations = store.List();
            Current = Conversations.Count > 0 ? store.Load(Conversations[0].Id) : null;
            if (Current == null) Current = NewConversation();
            Changed?.Invoke();
        }

        private static Conversation NewConversation() => new Conversation
        {
            Agent = AppSettings.DefaultAgent,
            Model = AppSettings.DefaultModel.Length > 0 ? AppSettings.DefaultModel : null,
            Effort = AppSettings.DefaultEffort.Length > 0 ? AppSettings.DefaultEffort : null,
        };

        public void New()
        {
            if (Running) return;
            Current = NewConversation();
            LatestImage = null;
            Changed?.Invoke();
        }

        public void Select(string id)
        {
            if (Running || _doc == null || Current?.Id == id) return;
            var loaded = new ConversationStore(new DocStringStore(_doc)).Load(id);
            if (loaded == null) return;
            Current = loaded;
            LatestImage = null;
            Changed?.Invoke();
        }

        public void DeleteCurrent()
        {
            if (Running || _doc == null) return;
            var store = new ConversationStore(new DocStringStore(_doc));
            store.Delete(Current.Id);
            Conversations = store.List();
            Current = Conversations.Count > 0 ? store.Load(Conversations[0].Id) : NewConversation();
            LatestImage = null;
            Changed?.Invoke();
        }

        /// <summary>Agent, model and effort are per conversation; they are saved with the next turn.</summary>
        public void Configure(string agent, string model, string effort)
        {
            if (Running || Current == null) return;
            Current.Agent = agent;
            Current.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            Current.Effort = string.IsNullOrEmpty(effort) ? null : effort;
            if (_doc != null && Current.Messages.Count > 0) Save();
        }

        private void Save()
        {
            var c = _live ?? Current;
            if (_doc == null || c == null) return;
            var store = new ConversationStore(new DocStringStore(_doc));
            store.Save(c);
            Conversations = store.List();
        }

        // ------------------------------------------------------------ turns

        /// <summary>Where files for the CLI go: how the plugin names the folder, and how the CLI does (they differ under Wine).</summary>
        private static void ExchangeDirs(out string local, out string host)
        {
            if (ProcessHost.IsWine)
            {
                host = "/tmp/rhinocc";
                local = ProcessHost.ToWinePath(host);
            }
            else
            {
                local = host = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhinocc");
            }
        }

        public void Send(string text, IList<ImageAttachment> images = null)
        {
            text = (text ?? "").Trim();
            images = images ?? new List<ImageAttachment>();
            Sync();
            if (Running || (text.Length == 0 && images.Count == 0) || Current == null) return;
            if (text.Length == 0) text = "(see the attached picture" + (images.Count > 1 ? "s" : "") + ")";
            Notice = null;
            if (!EnsureServer())
            {
                Changed?.Invoke();
                return;
            }

            var engine = Engines.Get(Current.Agent);
            // a session belongs to the agent that made it
            if (Current.SessionAgent != Current.Agent) Current.SessionId = null;
            var prompt = Current.SessionId == null && Current.Messages.Count > 0 ? Primer(Current) + text : text;

            ExchangeDirs(out var local, out var host);
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(local, "image-*")) System.IO.File.Delete(f);
            }
            catch { /* the folder may not exist yet */ }

            var turn = new Turn
            {
                Images = images.ToList(),
                ExchangeLocalDir = local,
                ExchangeHostDir = host,
                Prompt = prompt,
                Model = Current.Model,
                Effort = Current.Effort,
                SessionId = Current.SessionId,
                McpUrl = _server.Url(true),
                Instructions = BuildInstructions(),
                AllTools = AppSettings.AllTools,
            };
            if (turn.Instructions.Length > engine.InstructionLimit)
            {
                Notice = $"The instructions are {turn.Instructions.Length} characters; {engine.Label} takes at most {engine.InstructionLimit}. Shorten your custom instructions in the settings.";
                Changed?.Invoke();
                return;
            }

            _live = Current;
            var userMessage = Current.AddUser(text);
            Current.AddImages(userMessage, images);
            _liveMessage = Current.AddAssistant();
            Running = true;
            Save();

            Invocation invocation;
            try
            {
                invocation = engine.Prepare(turn);
            }
            catch (Exception e)
            {
                // e.g. the folder for the instructions or pictures could not be written
                OnEvent(AgentEvent.Of(AgentEventKind.Failed, "Could not prepare the request: " + e.Message));
                return;
            }
            _run = new AgentRun(engine, invocation, AppSettings.ForAgents());
            _run.Event += e => Application.Instance.AsyncInvoke(() => OnEvent(e));
            _run.Start();
            Changed?.Invoke();
        }

        public void Stop() => _run?.Cancel();

        private void OnEvent(AgentEvent e)
        {
            if (_live == null) return;
            MessageBuilder.Apply(_live, _liveMessage, e);
            if (e.Kind == AgentEventKind.ToolEnd && e.Parts != null)
            {
                var image = e.Parts.LastOrDefault(p => p.Type == "image");
                if (image != null) LatestImage = image.Data;
            }
            if (e.Kind == AgentEventKind.Done || e.Kind == AgentEventKind.Failed)
            {
                foreach (var b in _liveMessage.Blocks) b.Done = true;
                Running = false;
                Save();
                _live = null;
                _liveMessage = null;
                _run = null;
            }
            Changed?.Invoke();
        }

        /// <summary>When a conversation moves to another agent, it starts without memory; give it what was said.</summary>
        private static string Primer(Conversation c)
        {
            var sb = new StringBuilder("[Earlier in this conversation, with another assistant:\n");
            foreach (var m in c.Messages)
            {
                var text = string.Join("\n", m.Blocks.Where(b => b.Kind == "text").Select(b => b.Text)).Trim();
                if (text.Length == 0) continue;
                sb.Append(m.Role == "user" ? "User: " : "Assistant: ").Append(text).Append('\n');
            }
            var s = sb.ToString();
            if (s.Length > 6000) s = "[Earlier in this conversation, with another assistant (shortened):\n…" + s.Substring(s.Length - 6000);
            return s + "]\n\nNow the user says:\n";
        }
    }
}
