using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RhinoCC.Core.Agents
{
    /// <summary>Claude Code, started as <c>claude -p</c> with the plugin's MCP server and nothing else configured.</summary>
    public sealed class ClaudeEngine : IAgentEngine
    {
        public const string ServerName = "rhino";

        public string Id => "claude";
        public string Label => "Claude";
        public string Program => "claude";

        public IReadOnlyList<ModelInfo> Models { get; } = new[]
        {
            new ModelInfo { Id = "fable", Label = "Fable" },
            new ModelInfo { Id = "opus", Label = "Opus" },
            new ModelInfo { Id = "sonnet", Label = "Sonnet" },
            new ModelInfo { Id = "haiku", Label = "Haiku" },
        };

        public IReadOnlyList<string> Efforts { get; } = new[] { "low", "medium", "high", "xhigh", "max" };

        public Invocation Prepare(Turn turn)
        {
            var mcp = new JObject
            {
                ["mcpServers"] = new JObject { [ServerName] = new JObject { ["type"] = "http", ["url"] = turn.McpUrl } },
            }.ToString(Newtonsoft.Json.Formatting.None);

            var inv = new Invocation { Program = Program, Stdin = turn.Prompt };
            inv.Args.AddRange(new[]
            {
                "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                // without this the thinking arrives empty, so there is nothing to show
                "--thinking-display", "summarized",
                "--setting-sources", "",
                "--strict-mcp-config",
                "--mcp-config", mcp,
                "--allowedTools=mcp__" + ServerName + "__exec_rhino",
            });
            if (turn.AllTools)
            {
                inv.Args.AddRange(new[] { "--permission-mode", "auto" });
            }
            else
            {
                // ours, web and reading files; anything else is refused without asking
                inv.Args.AddRange(new[] { "--permission-mode", "dontAsk", "--tools", "WebSearch,WebFetch,Read,Glob,Grep", "--allowedTools", "WebSearch,WebFetch,Read,Glob,Grep" });
            }
            if (!string.IsNullOrEmpty(turn.Instructions))
                inv.Args.AddRange(new[] { "--append-system-prompt", turn.Instructions });
            if (!string.IsNullOrEmpty(turn.Model)) inv.Args.AddRange(new[] { "--model", turn.Model });
            if (!string.IsNullOrEmpty(turn.Effort)) inv.Args.AddRange(new[] { "--effort", turn.Effort });
            if (!string.IsNullOrEmpty(turn.SessionId)) inv.Args.AddRange(new[] { "--resume", turn.SessionId });
            return inv;
        }

        public IReadOnlyList<AgentEvent> Parse(string line)
        {
            var events = new List<AgentEvent>();
            JObject v;
            try { v = JObject.Parse(line); }
            catch { return events; }

            switch ((string)v["type"])
            {
                case "system":
                    if ((string)v["subtype"] == "init" && v["session_id"] != null)
                        events.Add(AgentEvent.Of(AgentEventKind.Session, (string)v["session_id"]));
                    break;
                case "stream_event":
                    var e = v["event"];
                    var et = (string)e?["type"];
                    var delta = e?["delta"];
                    if (et == "content_block_start" && (string)e["content_block"]?["type"] == "text")
                        events.Add(AgentEvent.Of(AgentEventKind.Break));
                    else if (et == "content_block_start" && (string)e["content_block"]?["type"] == "thinking")
                        events.Add(AgentEvent.Of(AgentEventKind.Thought, "\n\n"));
                    else if (et == "content_block_delta" && (string)delta?["type"] == "thinking_delta" && delta["thinking"] != null)
                        events.Add(AgentEvent.Of(AgentEventKind.Thought, (string)delta["thinking"]));
                    else if (et == "content_block_delta" && (string)delta?["type"] == "text_delta" && delta["text"] != null)
                        events.Add(AgentEvent.Of(AgentEventKind.Delta, (string)delta["text"]));
                    break;
                case "assistant":
                    if (v["message"]?["content"] is JArray blocks)
                        foreach (var b in blocks)
                        {
                            if ((string)b["type"] != "tool_use") continue;
                            var name = (string)b["name"] ?? "tool";
                            var prefix = "mcp__" + ServerName + "__";
                            if (name.StartsWith(prefix)) name = name.Substring(prefix.Length);
                            events.Add(new AgentEvent { Kind = AgentEventKind.ToolStart, Id = (string)b["id"], Name = name, Text = InputText(b["input"]) });
                        }
                    break;
                case "user":
                    if (v["message"]?["content"] is JArray results)
                        foreach (var b in results)
                        {
                            if ((string)b["type"] != "tool_result") continue;
                            events.Add(new AgentEvent
                            {
                                Kind = AgentEventKind.ToolEnd,
                                Id = (string)b["tool_use_id"],
                                Parts = Parts(b["content"]),
                                Error = (bool?)b["is_error"] == true,
                            });
                        }
                    break;
                case "result":
                    if ((bool?)v["is_error"] == true) events.Add(AgentEvent.Of(AgentEventKind.Failed, (string)v["result"] ?? "the agent failed"));
                    else events.Add(AgentEvent.Of(AgentEventKind.Done));
                    break;
            }
            return events;
        }

        /// <summary>The command line of an exec_rhino call, or the raw arguments of any other tool.</summary>
        internal static string InputText(JToken input)
        {
            if (input is JObject o && o.Count == 1 && o["args"]?.Type == JTokenType.String) return (string)o["args"];
            return input == null ? "" : input.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static List<ToolPart> Parts(JToken content)
        {
            var parts = new List<ToolPart>();
            if (content == null) return parts;
            if (content.Type == JTokenType.String)
            {
                parts.Add(new ToolPart { Type = "text", Text = (string)content });
                return parts;
            }
            if (content is JArray arr)
                foreach (var b in arr)
                {
                    var t = (string)b["type"];
                    if (t == "text") parts.Add(new ToolPart { Type = "text", Text = (string)b["text"] });
                    else if (t == "image")
                        parts.Add(new ToolPart { Type = "image", Mime = (string)b["source"]?["media_type"], Data = (string)b["source"]?["data"] });
                }
            return parts;
        }
    }
}
