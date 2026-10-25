using System.Linq;
using RhinoCC.Core.Agents;
using RhinoCC.Core.Conversations;
using Xunit;

namespace RhinoCC.Tests
{
    public class ClaudeTests
    {
        private static readonly ClaudeEngine Claude = new ClaudeEngine();

        [Fact]
        public void ReadsWhatClaudePrints()
        {
            var init = Claude.Parse(@"{""type"":""system"",""subtype"":""init"",""session_id"":""abc""}").Single();
            Assert.Equal(AgentEventKind.Session, init.Kind);
            Assert.Equal("abc", init.Text);

            Assert.Equal(AgentEventKind.Break, Claude.Parse(@"{""type"":""stream_event"",""event"":{""type"":""content_block_start"",""content_block"":{""type"":""text""}}}").Single().Kind);
            var d = Claude.Parse(@"{""type"":""stream_event"",""event"":{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""Hello""}}}").Single();
            Assert.Equal((AgentEventKind.Delta, "Hello"), (d.Kind, d.Text));
            var t = Claude.Parse(@"{""type"":""stream_event"",""event"":{""type"":""content_block_delta"",""delta"":{""type"":""thinking_delta"",""thinking"":""hmm""}}}").Single();
            Assert.Equal((AgentEventKind.Thought, "hmm"), (t.Kind, t.Text));

            var tool = Claude.Parse(@"{""type"":""assistant"",""message"":{""content"":[{""type"":""text"",""text"":""x""},{""type"":""tool_use"",""id"":""t1"",""name"":""mcp__rhino__exec_rhino"",""input"":{""args"":""status""}}]}}").Single();
            Assert.Equal(("t1", "exec_rhino", "status"), (tool.Id, tool.Name, tool.Text));

            var end = Claude.Parse(@"{""type"":""user"",""message"":{""content"":[{""type"":""tool_result"",""tool_use_id"":""t1"",""content"":[{""type"":""text"",""text"":""ok""},{""type"":""image"",""source"":{""type"":""base64"",""media_type"":""image/png"",""data"":""AAAA""}}]}]}}").Single();
            Assert.Equal(AgentEventKind.ToolEnd, end.Kind);
            Assert.Equal(new[] { "text", "image" }, end.Parts.Select(p => p.Type));
            Assert.Equal("AAAA", end.Parts[1].Data);

            Assert.Equal(AgentEventKind.Done, Claude.Parse(@"{""type"":""result"",""is_error"":false}").Single().Kind);
            var failed = Claude.Parse(@"{""type"":""result"",""is_error"":true,""result"":""Not logged in""}").Single();
            Assert.Equal((AgentEventKind.Failed, "Not logged in"), (failed.Kind, failed.Text));
            Assert.Empty(Claude.Parse("not json"));
            Assert.Empty(Claude.Parse(@"{""type"":""user""}"));
        }

        [Fact]
        public void PreparesTheCommandLine()
        {
            var inv = Claude.Prepare(new Turn { Prompt = "hi \"there\"", Model = "opus", Effort = "high", SessionId = "s1", McpUrl = "http://127.0.0.1:1/mcp/embedded", Instructions = "MANUAL", AllTools = false });
            var a = string.Join(" ", inv.Args);
            Assert.Equal("hi \"there\"", inv.Stdin);
            Assert.Contains("--resume s1", a);
            Assert.Contains("--model opus --effort high", a);
            Assert.Contains("--append-system-prompt MANUAL", a);
            Assert.Contains("--permission-mode dontAsk", a);
            Assert.Contains("--strict-mcp-config", a);
            var cfg = inv.Args[inv.Args.IndexOf("--mcp-config") + 1];
            Assert.Contains("\"url\":\"http://127.0.0.1:1/mcp/embedded\"", cfg);
            Assert.Contains("--permission-mode auto", string.Join(" ", Claude.Prepare(new Turn { McpUrl = "u", AllTools = true }).Args));
            Assert.DoesNotContain("--resume", string.Join(" ", Claude.Prepare(new Turn { McpUrl = "u" }).Args));
        }
    }

    public class CodexTests
    {
        private static readonly CodexEngine Codex = new CodexEngine();

        [Fact]
        public void ReadsWhatCodexPrints()
        {
            Assert.Equal("th1", Codex.Parse(@"{""type"":""thread.started"",""thread_id"":""th1""}").Single().Text);
            var start = Codex.Parse(@"{""type"":""item.started"",""item"":{""id"":""i1"",""type"":""mcp_tool_call"",""tool"":""exec_rhino"",""arguments"":{""args"":""layers""}}}").Single();
            Assert.Equal(("i1", "exec_rhino", "layers"), (start.Id, start.Name, start.Text));
            var end = Codex.Parse(@"{""type"":""item.completed"",""item"":{""id"":""i1"",""type"":""mcp_tool_call"",""status"":""completed"",""result"":{""content"":[{""type"":""text"",""text"":""Default""},{""type"":""image"",""mimeType"":""image/png"",""data"":""AA""}]}}}").Single();
            Assert.False(end.Error);
            Assert.Equal("image/png", end.Parts[1].Mime);
            Assert.True(Codex.Parse(@"{""type"":""item.completed"",""item"":{""id"":""i1"",""type"":""mcp_tool_call"",""status"":""failed"",""error"":{""message"":""boom""}}}").Single().Error);
            var msg = Codex.Parse(@"{""type"":""item.completed"",""item"":{""type"":""agent_message"",""text"":""done""}}");
            Assert.Equal(new[] { AgentEventKind.Break, AgentEventKind.Delta }, msg.Select(e => e.Kind));
            Assert.Equal(AgentEventKind.Done, Codex.Parse(@"{""type"":""turn.completed""}").Single().Kind);
            Assert.Equal("bad", Codex.Parse(@"{""type"":""turn.failed"",""error"":{""message"":""bad""}}").Single().Text);
        }

        [Fact]
        public void PreparesTheCommandLine()
        {
            var inv = Codex.Prepare(new Turn { Prompt = "hi", Model = "gpt-x", Effort = "low", SessionId = "th1", McpUrl = "http://127.0.0.1:1/mcp/embedded", Instructions = "line1\n\"q\"" });
            var a = inv.Args;
            Assert.Equal(new[] { "exec", "resume", "th1" }, a.Take(3));
            Assert.Contains("mcp_servers.rhino.url=\"http://127.0.0.1:1/mcp/embedded\"", a);
            Assert.Contains("developer_instructions=\"line1\\n\\\"q\\\"\"", a);
            Assert.Contains("sandbox_mode=\"read-only\"", a);
            Assert.Equal("-", a.Last());
            Assert.Equal("hi", inv.Stdin);
        }
    }

    public class LauncherTests
    {
        [Fact]
        public void QuotesByWindowsRules()
        {
            Assert.Equal("plain", ProcessHost.Quote("plain"));
            Assert.Equal("\"a b\"", ProcessHost.Quote("a b"));
            Assert.Equal("\"\"", ProcessHost.Quote(""));
            Assert.Equal("\"say \\\"hi\\\"\"", ProcessHost.Quote("say \"hi\""));
            Assert.Equal("\"C:\\dir with space\\\\\"", ProcessHost.Quote("C:\\dir with space\\"));
        }

        [Fact]
        public void UnderWineTheCliRunsThroughTheHostShell()
        {
            var inv = new Invocation { Program = "claude" };
            inv.Args.AddRange(new[] { "-p", "--model", "opus" });
            var psi = ProcessHost.Build(inv, new AgentSettings { ClaudePath = "/home/a/.local/bin/claude" }, wine: true, windows: true);
            Assert.Equal(@"Z:\bin\sh", psi.FileName);
            Assert.Equal("-lc \"cd /tmp && exec \\\"$0\\\" \\\"$@\\\"\" /home/a/.local/bin/claude -p --model opus", psi.Arguments);
        }

        [Fact]
        public void OnWindowsShimsGoThroughCmd()
        {
            var inv = new Invocation { Program = "codex" };
            inv.Args.Add("exec");
            var psi = ProcessHost.Build(inv, new AgentSettings(), wine: false, windows: true);
            Assert.Equal("cmd.exe", psi.FileName);
            Assert.Equal("/c codex exec", psi.Arguments);
        }
    }

    public class ConversationTests
    {
        private sealed class Memory : IKeyValueStore
        {
            public readonly System.Collections.Generic.Dictionary<string, string> Data = new System.Collections.Generic.Dictionary<string, string>();
            public string Get(string key) => Data.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => Data[key] = value;
            public void Delete(string key) => Data.Remove(key);
        }

        [Fact]
        public void BuildsAMessageFromEvents()
        {
            var c = new Conversation();
            c.AddUser("hello there");
            var m = c.AddAssistant();
            void Apply(AgentEvent e) => MessageBuilder.Apply(c, m, e);
            Apply(AgentEvent.Of(AgentEventKind.Session, "s9"));
            Apply(AgentEvent.Of(AgentEventKind.Thought, "think"));
            Apply(AgentEvent.Of(AgentEventKind.Break));
            Apply(AgentEvent.Of(AgentEventKind.Delta, "Hel"));
            Apply(AgentEvent.Of(AgentEventKind.Delta, "lo"));
            Apply(new AgentEvent { Kind = AgentEventKind.ToolStart, Id = "t", Name = "exec_rhino", Text = "status" });
            Apply(new AgentEvent { Kind = AgentEventKind.ToolEnd, Id = "t", Parts = new System.Collections.Generic.List<ToolPart> { new ToolPart { Type = "text", Text = "ok" } } });
            Apply(AgentEvent.Of(AgentEventKind.Break));
            Apply(AgentEvent.Of(AgentEventKind.Delta, "Next"));

            Assert.Equal("s9", c.SessionId);
            Assert.Equal("hello there", c.Title);
            Assert.Equal(new[] { "thought", "text", "tool", "text" }, m.Blocks.Select(b => b.Kind));
            Assert.Equal("Hello", m.Blocks[1].Text);
            Assert.True(m.Blocks[2].Done);
            Assert.Equal("Next", m.Blocks[3].Text);
        }

        [Fact]
        public void SavesWithoutPicturesAndKeepsAnIndex()
        {
            var mem = new Memory();
            var store = new ConversationStore(mem);
            var c = new Conversation { Title = "A" };
            var m = c.AddAssistant();
            m.Blocks.Add(new Block { Kind = "tool", ToolId = "t", Output = new System.Collections.Generic.List<ToolPart> { new ToolPart { Type = "image", Mime = "image/png", Data = "HUGE" } } });
            store.Save(c);
            store.Save(new Conversation { Title = "B" });

            Assert.DoesNotContain("HUGE", string.Concat(mem.Data.Values));
            Assert.Equal("[picture]", store.Load(c.Id).Messages[0].Blocks[0].Output[0].Text);
            Assert.Equal(new[] { "B", "A" }, store.List().Select(i => i.Title));
            store.Delete(c.Id);
            Assert.Null(store.Load(c.Id));
            Assert.Equal(new[] { "B" }, store.List().Select(i => i.Title));
        }

        [Fact]
        public void CorruptStorageIsIgnored()
        {
            var mem = new Memory();
            mem.Data["RhinoCC.index"] = "{{{";
            Assert.Empty(new ConversationStore(mem).List());
        }
    }
}

namespace RhinoCC.Tests
{
    public class TranscriptTests
    {
        [Fact]
        public void RendersMessagesToolsAndFailures()
        {
            var c = new Conversation();
            c.AddUser("hi");
            var m = c.AddAssistant();
            m.Blocks.Add(new Block { Kind = "text", Text = "Looking.", Done = true });
            m.Blocks.Add(new Block { Kind = "tool", ToolName = "exec_rhino", Text = "status", Done = true, Output = new System.Collections.Generic.List<ToolPart> { new ToolPart { Type = "text", Text = "Rhino 8" }, new ToolPart { Type = "image" } } });
            m.Blocks.Add(new Block { Kind = "tool", ToolName = "exec_rhino", Text = "shot" });
            m.Failure = "Stopped.";
            var text = TranscriptRenderer.Render(c, "Claude", true);
            Assert.Contains("YOU\nhi", text);
            Assert.Contains("CLAUDE\nLooking.", text);
            Assert.Contains("▸ exec_rhino: status\n    Rhino 8\n    [picture shown below]", text);
            Assert.Contains("▸ exec_rhino: shot  (running)", text);
            Assert.Contains("⚠ Stopped.", text);
        }
    }
}
