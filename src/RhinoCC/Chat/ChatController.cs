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
        private bool _compactTurn;

        /// <summary>A conversation compacts itself once the agent's memory is this full (0 to 1).</summary>
        public const double AutoCompactAt = 0.8;
        private const string CompactPrompt =
            "Write a summary of this conversation for your own future reference, as notes that let you carry on without " +
            "the earlier messages: what the user wants, every measurement and decision, what is in the Rhino model now, and what is " +
            "still open. Do not use any tools. Plain text, under 400 words.";

        public Conversation Current { get; private set; }
        public List<ConversationInfo> Conversations { get; private set; } = new List<ConversationInfo>();
        public bool Running { get; private set; }
        public DateTime RunStarted { get; private set; }
        public string Notice { get; private set; }
        /// <summary>The running turn is a compaction, not an answer.</summary>
        public bool Compacting => Running && _compactTurn;
        /// <summary>The account's limits as the agents last reported them (plan windows), by agent id.</summary>
        public List<LimitInfo> Limits(string agent) => AppSettings.LimitsOf(agent).Where(l => !l.ResetsAt.HasValue || l.ResetsAt.Value * 1000 > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToList();

        /// <summary>Raised on the UI thread whenever what the panel shows has changed.</summary>
        public event Action Changed;

        private ChatController()
        {
            _cli = RhinoCommands.Build();
            _server = new McpServer(_cli, () => BuildInstructions());
        }

        public string McpUrl => _server.IsRunning ? _server.Url(false) : null;

        public string BuildInstructions(bool onUiThread = false)
        {
            var sb = new StringBuilder();
            var custom = AppSettings.Instructions.Trim();
            if (custom.Length > 0) sb.Append(custom).Append("\n\n");
            var env = RhinoCommands.EnvironmentInfo(onUiThread);
            if (!string.IsNullOrEmpty(env)) sb.Append(env).Append("\n\n");
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
        public void Sync() => Sync(RhinoDoc.ActiveDoc, false);

        /// <summary>
        /// The document became active while its file was still being read (it is active but empty at first),
        /// so once Rhino says it finished opening, its conversations have to be read again.
        /// </summary>
        public void Reload(RhinoDoc doc) => Sync(doc, true);

        private void Sync(RhinoDoc doc, bool force)
        {
            if (doc == null || (!force && _doc != null && _doc.RuntimeSerialNumber == doc.RuntimeSerialNumber && Current != null)) return;
            if (Running) return;
            if (force && RhinoDoc.ActiveDoc != null && RhinoDoc.ActiveDoc.RuntimeSerialNumber != doc.RuntimeSerialNumber) return;
            _doc = doc;
            var store = new ConversationStore(new DocStringStore(doc));
            Conversations = store.List();
            RhinoCC.Core.Log.Write($"document '{doc.Name ?? "(untitled)"}' (#{doc.RuntimeSerialNumber}): {Conversations.Count} saved conversation(s)");
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
            Changed?.Invoke();
        }

        public void Select(string id)
        {
            if (Running || _doc == null || Current?.Id == id) return;
            var loaded = new ConversationStore(new DocStringStore(_doc)).Load(id);
            if (loaded == null) return;
            Current = loaded;
            Changed?.Invoke();
        }

        public void DeleteCurrent()
        {
            if (Running || _doc == null) return;
            var store = new ConversationStore(new DocStringStore(_doc));
            store.Delete(Current.Id);
            Conversations = store.List();
            Current = Conversations.Count > 0 ? store.Load(Conversations[0].Id) : NewConversation();
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

        public void SetAutoCompact(bool on)
        {
            if (Current == null) return;
            Current.AutoCompact = on;
            if (_doc != null && Current.Messages.Count > 0) Save();
            Changed?.Invoke();
        }

        private void Save()
        {
            var c = _live ?? Current;
            if (_doc == null || c == null) return;
            var store = new ConversationStore(new DocStringStore(_doc));
            store.Save(c);
            RhinoCC.Core.Log.Write($"saved conversation '{c.Title}' into '{_doc.Name ?? "(untitled)"}'");
            Conversations = store.List();
            // the strings are part of the file, but Rhino only writes a document it considers changed
            _doc.Modified = true;
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
            RhinoCC.Core.Log.Write($"send ({Current.Agent}, model {Current.Model ?? "default"}, effort {Current.Effort ?? "default"}): {text}");
            try { SendCore(text, images); }
            catch (Exception e)
            {
                RhinoCC.Core.Log.Write("send failed: " + e);
                // anything unexpected belongs in the chat, not in the log
                if (_live == null) Reject(text, images, "Could not send: " + e.Message);
                else OnEvent(AgentEvent.Of(AgentEventKind.Failed, "Could not send: " + e.Message));
            }
        }

        /// <summary>Shows the user's message with the reason it was not sent, in the conversation itself.</summary>
        private void Reject(string text, IList<ImageAttachment> images, string reason)
        {
            var user = Current.AddUser(text);
            Current.AddImages(user, images);
            RhinoCC.Core.Log.Write("rejected: " + reason);
            Current.AddAssistant().Failure = reason;
            Notice = null;
            Save();
            Changed?.Invoke();
        }

        private void SendCore(string text, IList<ImageAttachment> images)
        {
            if (!EnsureServer())
            {
                Reject(text, images, Notice);
                return;
            }

            var engine = Engines.Get(Current.Agent);
            // a session belongs to the agent that made it
            if (Current.SessionAgent != Current.Agent) Current.SessionId = null;
            var prompt = Current.SessionId == null && Current.Messages.Count > 0 ? Primer(Current) + text : text;

            var turn = BuildTurn(engine, prompt, images);
            if (turn.Instructions.Length > engine.InstructionLimit)
            {
                Reject(text, images, $"The instructions are {turn.Instructions.Length} characters; {engine.Label} takes at most {engine.InstructionLimit}. Shorten your custom instructions in the settings.");
                return;
            }

            _live = Current;
            var userMessage = Current.AddUser(text);
            Current.AddImages(userMessage, images);
            _liveMessage = Current.AddAssistant();
            _compactTurn = false;
            Running = true;
            RunStarted = DateTime.UtcNow;
            Save();
            Launch(engine, turn);
        }

        private Turn BuildTurn(IAgentEngine engine, string prompt, IList<ImageAttachment> images)
        {
            RhinoCC.Core.Log.Write($"server: {_server.Url(true)}");
            ExchangeDirs(out var local, out var host);
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(local, "image-*")) System.IO.File.Delete(f);
            }
            catch { /* the folder may not exist yet */ }

            return new Turn
            {
                Images = images.ToList(),
                ExchangeLocalDir = local,
                ExchangeHostDir = host,
                Prompt = prompt,
                Model = Current.Model,
                Effort = Current.Effort,
                SessionId = Current.SessionId,
                McpUrl = _server.Url(true),
                Instructions = BuildInstructions(true),
                AllTools = AppSettings.AllTools,
            };
        }

        private void Launch(IAgentEngine engine, Turn turn)
        {
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

        /// <summary>Replaces the agent's memory of the conversation with a summary it writes itself.</summary>
        public void Compact()
        {
            Sync();
            if (Running || Current == null || Current.SessionId == null || Current.SessionAgent != Current.Agent) return;
            if (!EnsureServer())
            {
                Changed?.Invoke();
                return;
            }
            Notice = null;
            RhinoCC.Core.Log.Write("compacting '" + Current.Title + "'");
            var engine = Engines.Get(Current.Agent);
            try
            {
                var turn = BuildTurn(engine, CompactPrompt, new List<ImageAttachment>());
                _live = Current;
                _liveMessage = new ChatMessage { Role = "assistant" }; // not part of the conversation; its text becomes the summary
                _compactTurn = true;
                Running = true;
                RunStarted = DateTime.UtcNow;
                Launch(engine, turn);
            }
            catch (Exception e)
            {
                RhinoCC.Core.Log.Write("compact failed: " + e);
                Notice = "Could not compact: " + e.Message;
                Running = false;
                _live = null;
                _liveMessage = null;
                Changed?.Invoke();
            }
        }

        public void Stop() => _run?.Cancel();

        private void OnEvent(AgentEvent e)
        {
            if (_live == null) return;
            if (e.Kind == AgentEventKind.Failed || e.Kind == AgentEventKind.Warn) RhinoCC.Core.Log.Write($"{e.Kind}: {e.Text}");
            if (e.Kind == AgentEventKind.Usage)
            {
                ApplyUsage(e.Usage);
                Changed?.Invoke();
                return;
            }
            MessageBuilder.Apply(_live, _liveMessage, e);
            if (e.Kind == AgentEventKind.Done || e.Kind == AgentEventKind.Failed)
            {
                var conversation = _live;
                var message = _liveMessage;
                var compact = _compactTurn;
                foreach (var b in message.Blocks) b.Done = true;
                Running = false;
                _compactTurn = false;
                _live = null;
                _liveMessage = null;
                _run = null;
                if (compact) FinishCompact(conversation, message, e);
                else
                {
                    Save();
                    if (e.Kind == AgentEventKind.Done)
                    {
                        MaybeChime();
                        // the memory is nearly full: tidy it before the next question
                        if (Current == conversation && conversation.NeedsCompact(conversation.Agent, AutoCompactAt))
                        {
                            Changed?.Invoke();
                            Compact();
                            return;
                        }
                    }
                }
            }
            Changed?.Invoke();
        }

        private void FinishCompact(Conversation c, ChatMessage message, AgentEvent e)
        {
            var summary = string.Join("\n", message.Blocks.Where(b => b.Kind == "text").Select(b => b.Text)).Trim();
            if (e.Kind == AgentEventKind.Failed || summary.Length == 0)
            {
                if (e.Kind == AgentEventKind.Failed && e.Text != "Stopped.") Notice = "Could not compact: " + e.Text;
                return;
            }
            c.Summary = summary;
            c.SessionId = null;
            c.SessionAgent = null;
            c.Context = null;
            c.AddNote("conversation compacted");
            Save();
        }

        private void ApplyUsage(UsageInfo u)
        {
            if (u == null) return;
            if (u.Limits.Count > 0) AppSettings.SetLimits(_live?.Agent ?? Current.Agent, u.Limits);
            // the size of a compaction turn says nothing about the conversation that is about to be replaced
            if (_compactTurn || _live == null) return;
            if (u.ContextUsed != null || u.ContextWindow != null)
            {
                var old = _live.Context != null && _live.Context.Agent == _live.Agent ? _live.Context : null;
                var window = u.ContextWindow ?? old?.Window ?? 0;
                var used = u.ContextUsed ?? old?.Used ?? 0;
                if (window > 0) _live.Context = new ContextUse { Agent = _live.Agent, Used = used, Window = window };
            }
        }

        // ------------------------------------------------------------ chime

        /// <summary>A reply that took a while ends with a chime. Not an agent tool: the plugin decides.</summary>
        private void MaybeChime()
        {
            if (!AppSettings.ChimeOn) return;
            if ((DateTime.UtcNow - RunStarted).TotalSeconds < AppSettings.ChimeSecs) return;
            Chime.Play();
        }

        /// <summary>When a conversation moves to another agent, it starts without memory; give it what was said.</summary>
        private static string Primer(Conversation c)
        {
            var sb = new StringBuilder();
            var messages = c.Messages.Where(m => m.Role != "note").ToList();
            if (!string.IsNullOrEmpty(c.Summary))
            {
                sb.Append("[Notes you wrote earlier in this conversation, to carry on without the first messages:\n").Append(c.Summary).Append("]\n\n");
                // only what was said after the compaction is not in the notes
                var lastNote = c.Messages.FindLastIndex(m => m.Role == "note");
                messages = c.Messages.Skip(lastNote + 1).Where(m => m.Role != "note").ToList();
                if (messages.Count == 0) return sb.Append("Now the user says:\n").ToString();
            }
            sb.Append("[Earlier in this conversation, with another assistant:\n");
            foreach (var m in messages)
            {
                var text = string.Join("\n", m.Blocks.Where(b => b.Kind == "text").Select(b => b.Text)).Trim();
                if (text.Length == 0) continue;
                sb.Append(m.Role == "user" ? "User: " : "Assistant: ").Append(text).Append('\n');
            }
            var s = sb.ToString();
            if (s.Length > 6000) s = s.Substring(0, Math.Min(s.IndexOf("[Earlier", StringComparison.Ordinal), s.Length)) + "[Earlier in this conversation (shortened):\n…" + s.Substring(s.Length - 6000);
            return s + "]\n\nNow the user says:\n";
        }
    }
}
