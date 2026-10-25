using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoCC.Core.Agents
{
    /// <summary>
    /// Codex, started as <c>codex exec --json</c>. Everything is passed as <c>-c</c> overrides, so the user's
    /// own config.toml and sign-in stay untouched (any MCP servers they configured there still load).
    /// </summary>
    public sealed class CodexEngine : IAgentEngine
    {
        public const string ServerName = "rhino";

        public string Id => "codex";
        public string Label => "Codex";
        public string Program => "codex";

        // Codex's model list changes often and `codex debug models` lists the real one; blank means "its default".
        public IReadOnlyList<ModelInfo> Models { get; } = new ModelInfo[0];

        public IReadOnlyList<string> Efforts { get; } = new[] { "low", "medium", "high" };

        private static string Toml(string value) => JsonConvert.SerializeObject(value);

        public Invocation Prepare(Turn turn)
        {
            var inv = new Invocation { Program = Program, Stdin = turn.Prompt };
            inv.Args.Add("exec");
            if (!string.IsNullOrEmpty(turn.SessionId))
            {
                inv.Args.Add("resume");
                inv.Args.Add(turn.SessionId);
            }
            if (!string.IsNullOrEmpty(turn.Model)) inv.Args.AddRange(new[] { "-m", turn.Model });
            if (!string.IsNullOrEmpty(turn.Effort)) inv.Args.AddRange(new[] { "-c", "model_reasoning_effort=" + Toml(turn.Effort) });
            inv.Args.AddRange(new[]
            {
                // ask for the reasoning summaries, so there is something to show while it thinks
                "-c", "model_reasoning_summary=\"auto\"",
                "-c", "web_search=\"live\"",
                "-c", "sandbox_mode=" + Toml(turn.AllTools ? "workspace-write" : "read-only"),
                "-c", "mcp_servers." + ServerName + ".url=" + Toml(turn.McpUrl),
                "-c", "mcp_servers." + ServerName + ".default_tools_approval_mode=\"approve\"",
            });
            if (!string.IsNullOrEmpty(turn.Instructions))
                inv.Args.AddRange(new[] { "-c", "developer_instructions=" + Toml(turn.Instructions) });
            inv.Args.AddRange(new[] { "--json", "--skip-git-repo-check", "-" });
            return inv;
        }

        public IReadOnlyList<AgentEvent> Parse(string line)
        {
            var events = new List<AgentEvent>();
            JObject v;
            try { v = JObject.Parse(line); }
            catch { return events; }

            var item = v["item"];
            var itemType = (string)item?["type"];
            switch ((string)v["type"])
            {
                case "thread.started":
                    if (v["thread_id"] != null) events.Add(AgentEvent.Of(AgentEventKind.Session, (string)v["thread_id"]));
                    break;
                case "item.started":
                    if (itemType == "mcp_tool_call")
                        events.Add(new AgentEvent { Kind = AgentEventKind.ToolStart, Id = (string)item["id"], Name = (string)item["tool"] ?? "tool", Text = ClaudeEngine.InputText(item["arguments"]) });
                    break;
                case "item.completed":
                    if (itemType == "mcp_tool_call")
                    {
                        var failed = (string)item["status"] == "failed" || (item["error"] != null && item["error"].Type != JTokenType.Null);
                        var parts = Parts(item["result"]?["content"]);
                        var message = (string)item["error"]?["message"];
                        if (message != null) parts.Add(new ToolPart { Type = "text", Text = message });
                        events.Add(new AgentEvent { Kind = AgentEventKind.ToolEnd, Id = (string)item["id"], Parts = parts, Error = failed });
                    }
                    else if (itemType == "agent_message" && item["text"] != null)
                    {
                        // a whole message at a time, not word by word
                        events.Add(AgentEvent.Of(AgentEventKind.Break));
                        events.Add(AgentEvent.Of(AgentEventKind.Delta, (string)item["text"]));
                    }
                    else if (itemType == "reasoning" && item["text"] != null)
                        events.Add(AgentEvent.Of(AgentEventKind.Thought, "\n\n" + (string)item["text"]));
                    else if (itemType == "error" && item["message"] != null)
                        events.Add(AgentEvent.Of(AgentEventKind.Warn, (string)item["message"]));
                    break;
                case "turn.completed":
                    events.Add(AgentEvent.Of(AgentEventKind.Done));
                    break;
                case "turn.failed":
                    events.Add(AgentEvent.Of(AgentEventKind.Failed, (string)v["error"]?["message"] ?? "the agent failed"));
                    break;
                case "error":
                    if (v["message"] != null) events.Add(AgentEvent.Of(AgentEventKind.Warn, (string)v["message"]));
                    break;
            }
            return events;
        }

        private static List<ToolPart> Parts(JToken content)
        {
            var parts = new List<ToolPart>();
            if (content is JArray arr)
                foreach (var b in arr)
                {
                    var t = (string)b["type"];
                    if (t == "text") parts.Add(new ToolPart { Type = "text", Text = (string)b["text"] });
                    else if (t == "image") parts.Add(new ToolPart { Type = "image", Mime = (string)b["mimeType"], Data = (string)b["data"] });
                }
            return parts;
        }
    }
}
