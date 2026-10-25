using System.Linq;
using RhinoCC.Core.Agents;
using RhinoCC.Core.Conversations;
using Xunit;

namespace RhinoCC.Tests
{
    public class UsageTests
    {
        [Fact]
        public void Claude_reports_plan_limits_as_percent()
        {
            var e = new ClaudeEngine().Parse(@"{""type"":""rate_limit_event"",""rate_limit_info"":{""unifiedWindows"":{""five_hour"":{""utilization"":0.57,""resetsAt"":5},""seven_day"":{""utilization"":0.13,""resetsAt"":9}}}}").Single();
            Assert.Equal(AgentEventKind.Usage, e.Kind);
            Assert.Equal(new[] { "5h", "weekly" }, e.Usage.Limits.Select(l => l.Label));
            Assert.Equal(57, e.Usage.Limits[0].Percent, 6);
            Assert.Equal(9, e.Usage.Limits[1].ResetsAt);
        }

        [Fact]
        public void Claude_context_is_the_tokens_of_the_last_message_and_the_window_comes_with_the_result()
        {
            var used = new ClaudeEngine().Parse(@"{""type"":""assistant"",""message"":{""usage"":{""input_tokens"":10,""cache_read_input_tokens"":100,""cache_creation_input_tokens"":5,""output_tokens"":2},""content"":[]}}").Single();
            Assert.Equal(117, used.Usage.ContextUsed);
            var done = new ClaudeEngine().Parse(@"{""type"":""result"",""is_error"":false,""modelUsage"":{""m"":{""contextWindow"":1000}}}");
            Assert.Equal(1000, done[0].Usage.ContextWindow);
            Assert.Equal(AgentEventKind.Done, done[1].Kind);
        }

        [Fact]
        public void Codex_usage_comes_from_the_last_token_count_of_its_record()
        {
            var record = @"{""payload"":{""type"":""token_count"",""info"":{""last_token_usage"":{""total_tokens"":1},""model_context_window"":5}}}" + "\n" +
                @"{""payload"":{""type"":""token_count"",""info"":{""last_token_usage"":{""total_tokens"":22558},""model_context_window"":475000},""rate_limits"":{""primary"":{""used_percent"":5.0,""window_minutes"":300,""resets_at"":10},""secondary"":{""used_percent"":21.0,""window_minutes"":10080,""resets_at"":20}}}}" + "\n";
            var u = CodexEngine.ReadUsage(record);
            Assert.Equal(22558, u.ContextUsed);
            Assert.Equal(475000, u.ContextWindow);
            Assert.Equal(new[] { "5h", "weekly" }, u.Limits.Select(l => l.Label));
            Assert.Equal(21, u.Limits[1].Percent);
        }

        [Fact]
        public void A_conversation_compacts_only_when_asked_to_and_for_the_agent_that_measured()
        {
            var c = new Conversation { Context = new ContextUse { Agent = "claude", Used = 80, Window = 100 } };
            Assert.False(c.NeedsCompact("claude", 0.8));
            c.AutoCompact = true;
            Assert.True(c.NeedsCompact("claude", 0.8));
            Assert.False(c.NeedsCompact("codex", 0.8));
            c.Context.Used = 79;
            Assert.False(c.NeedsCompact("claude", 0.8));
        }

        [Fact]
        public void A_note_is_drawn_between_the_messages()
        {
            var c = new Conversation();
            c.AddUser("hi");
            c.AddNote("conversation compacted");
            Assert.Contains("conversation compacted", RtfRenderer.Render(c, "Claude", false));
        }
    }
}
