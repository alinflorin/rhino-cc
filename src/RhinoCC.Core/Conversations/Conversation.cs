using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RhinoCC.Core.Agents;

namespace RhinoCC.Core.Conversations
{
    public sealed class Block
    {
        /// <summary>"text", "thought" or "tool".</summary>
        public string Kind { get; set; }
        public string Text { get; set; }
        public string ToolId { get; set; }
        public string ToolName { get; set; }
        public List<ToolPart> Output { get; set; }
        public bool Error { get; set; }
        public bool Done { get; set; }
    }

    public sealed class ChatMessage
    {
        /// <summary>"user" or "assistant".</summary>
        public string Role { get; set; }
        public List<Block> Blocks { get; set; } = new List<Block>();
        /// <summary>Set when the turn failed or was stopped.</summary>
        public string Failure { get; set; }
    }

    public sealed class Conversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New conversation";
        public string Agent { get; set; } = "claude";
        public string Model { get; set; }
        public string Effort { get; set; }
        /// <summary>The agent's own id for this conversation, so the next turn continues it.</summary>
        public string SessionId { get; set; }
        /// <summary>Which agent <see cref="SessionId"/> belongs to; another agent cannot continue it.</summary>
        public string SessionAgent { get; set; }
        public DateTime Updated { get; set; } = DateTime.UtcNow;
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        public ChatMessage AddUser(string text)
        {
            var m = new ChatMessage { Role = "user" };
            m.Blocks.Add(new Block { Kind = "text", Text = text, Done = true });
            Messages.Add(m);
            if (Messages.Count(x => x.Role == "user") == 1)
                Title = Truncate(text.Replace('\n', ' ').Trim(), 40);
            Updated = DateTime.UtcNow;
            return m;
        }

        public ChatMessage AddAssistant()
        {
            var m = new ChatMessage { Role = "assistant" };
            Messages.Add(m);
            return m;
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }

    /// <summary>Folds the events of a turn into the assistant message they build.</summary>
    public static class MessageBuilder
    {
        public static void Apply(Conversation conversation, ChatMessage message, AgentEvent e)
        {
            switch (e.Kind)
            {
                case AgentEventKind.Session:
                    conversation.SessionId = e.Text;
                    conversation.SessionAgent = conversation.Agent;
                    break;
                case AgentEventKind.Break:
                    var last = message.Blocks.LastOrDefault();
                    if (last != null && last.Kind != "tool") last.Done = true;
                    break;
                case AgentEventKind.Delta:
                    Append(message, "text", e.Text);
                    break;
                case AgentEventKind.Thought:
                    Append(message, "thought", e.Text);
                    break;
                case AgentEventKind.ToolStart:
                    message.Blocks.Add(new Block { Kind = "tool", ToolId = e.Id, ToolName = e.Name, Text = e.Text });
                    break;
                case AgentEventKind.ToolEnd:
                    var tool = message.Blocks.LastOrDefault(b => b.Kind == "tool" && b.ToolId == e.Id);
                    if (tool != null)
                    {
                        tool.Output = e.Parts;
                        tool.Error = e.Error;
                        tool.Done = true;
                    }
                    break;
                case AgentEventKind.Warn:
                    message.Blocks.Add(new Block { Kind = "text", Text = "⚠ " + e.Text, Done = true });
                    break;
                case AgentEventKind.Failed:
                    message.Failure = e.Text;
                    break;
            }
            conversation.Updated = DateTime.UtcNow;
        }

        private static void Append(ChatMessage m, string kind, string text)
        {
            var last = m.Blocks.LastOrDefault();
            if (last == null || last.Kind != kind || last.Done)
            {
                last = new Block { Kind = kind };
                m.Blocks.Add(last);
            }
            last.Text += text;
        }
    }

    /// <summary>Where conversations live; the plugin backs this with the .3dm's user strings.</summary>
    public interface IKeyValueStore
    {
        string Get(string key);
        void Set(string key, string value);
        void Delete(string key);
    }

    public sealed class ConversationInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime Updated { get; set; }
    }

    public sealed class ConversationStore
    {
        private const string IndexKey = "RhinoCC.index";
        private const string Prefix = "RhinoCC.conversation.";
        private readonly IKeyValueStore _store;

        public ConversationStore(IKeyValueStore store)
        {
            _store = store;
        }

        public List<ConversationInfo> List()
        {
            var json = _store.Get(IndexKey);
            if (string.IsNullOrEmpty(json)) return new List<ConversationInfo>();
            try { return JsonConvert.DeserializeObject<List<ConversationInfo>>(json) ?? new List<ConversationInfo>(); }
            catch { return new List<ConversationInfo>(); }
        }

        public Conversation Load(string id)
        {
            var json = _store.Get(Prefix + id);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonConvert.DeserializeObject<Conversation>(json); }
            catch { return null; }
        }

        public void Save(Conversation c)
        {
            // pictures are for the screen only: a few of them would double the size of the model file
            var copy = JsonConvert.DeserializeObject<Conversation>(JsonConvert.SerializeObject(c));
            foreach (var part in copy.Messages.SelectMany(m => m.Blocks).Where(b => b.Output != null).SelectMany(b => b.Output))
            {
                if (part.Type != "image") continue;
                part.Type = "text";
                part.Text = "[picture]";
                part.Data = null;
            }
            _store.Set(Prefix + c.Id, JsonConvert.SerializeObject(copy));

            var index = List();
            index.RemoveAll(i => i.Id == c.Id);
            index.Insert(0, new ConversationInfo { Id = c.Id, Title = c.Title, Updated = c.Updated });
            _store.Set(IndexKey, JsonConvert.SerializeObject(index));
        }

        public void Delete(string id)
        {
            _store.Delete(Prefix + id);
            var index = List();
            index.RemoveAll(i => i.Id == id);
            _store.Set(IndexKey, JsonConvert.SerializeObject(index));
        }
    }
}
