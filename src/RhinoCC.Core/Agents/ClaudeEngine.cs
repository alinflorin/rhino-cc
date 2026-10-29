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

        // travels as an argument, so it shares the command line (Windows: 32K characters) with everything else
        public int InstructionLimit => 29000;

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
            if (turn.Images.Count > 0)
            {
                // pictures need the structured input; the CLI reads one message per line
                inv.Args.AddRange(new[] { "--input-format", "stream-json" });
                var content = new JArray();
                foreach (var img in turn.Images)
                    content.Add(new JObject { ["type"] = "image", ["source"] = new JObject { ["type"] = "base64", ["media_type"] = img.Mime, ["data"] = img.Base64 } });
                content.Add(new JObject { ["type"] = "text", ["text"] = turn.Prompt });
                inv.Stdin = new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user", ["content"] = content } }
                    .ToString(Newtonsoft.Json.Formatting.None) + "\n";
            }
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
                    var e = v["event"] as JObject; // fields below can be null or plain values
                    var et = (string)e?["type"];
                    var delta = e?["delta"] as JObject;
                    if (et == "content_block_start" && (string)(e["content_block"] as JObject)?["type"] == "text")
                        events.Add(AgentEvent.Of(AgentEventKind.Break));
                    else if (et == "content_block_start" && (string)(e["content_block"] as JObject)?["type"] == "thinking")
                        events.Add(AgentEvent.Of(AgentEventKind.Thought, "\n\n"));
                    else if (et == "content_block_delta" && (string)delta?["type"] == "thinking_delta" && delta["thinking"] != null)
                        events.Add(AgentEvent.Of(AgentEventKind.Thought, (string)delta["thinking"]));
                    else if (et == "content_block_delta" && (string)delta?["type"] == "text_delta" && delta["text"] != null)
                        events.Add(AgentEvent.Of(AgentEventKind.Delta, (string)delta["text"]));
                    break;
                case "rate_limit_event":
                    var windows = (v["rate_limit_info"] as JObject)?["unifiedWindows"] as JObject;
                    var usage = new UsageInfo();
                    foreach (var pair in new[] { new[] { "five_hour", "5h" }, new[] { "seven_day", "weekly" } })
                    {
                        var w = windows?[pair[0]] as JObject;
                        if (w?["utilization"] == null) continue;
                        usage.Limits.Add(new LimitInfo { Label = pair[1], Percent = (double)w["utilization"] * 100, ResetsAt = (long?)w["resetsAt"] });
                    }
                    if (usage.Limits.Count > 0) events.Add(new AgentEvent { Kind = AgentEventKind.Usage, Usage = usage });
                    break;
                case "assistant":
                    if ((v["message"] as JObject)?["usage"] is JObject u)
                    {
                        long N(string k) => (long?)u[k] ?? 0;
                        events.Add(new AgentEvent
                        {
                            Kind = AgentEventKind.Usage,
                            Usage = new UsageInfo { ContextUsed = N("input_tokens") + N("cache_read_input_tokens") + N("cache_creation_input_tokens") + N("output_tokens") },
                        });
                    }
                    if ((v["message"] as JObject)?["content"] is JArray blocks)
                        foreach (var b in blocks)
                        {
                            if (!(b is JObject) || (string)b["type"] != "tool_use") continue;
                            var name = (string)b["name"] ?? "tool";
                            var prefix = "mcp__" + ServerName + "__";
                            if (name.StartsWith(prefix)) name = name.Substring(prefix.Length);
                            events.Add(new AgentEvent { Kind = AgentEventKind.ToolStart, Id = (string)b["id"], Name = name, Text = InputText(b["input"]) });
                        }
                    break;
                case "user":
                    if ((v["message"] as JObject)?["content"] is JArray results)
                        foreach (var b in results)
                        {
                            if (!(b is JObject) || (string)b["type"] != "tool_result") continue;
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
                    else
                    {
                        long? window = null;
                        if (v["modelUsage"] is JObject mu)
                            foreach (var m in mu.Properties())
                                if (window == null) window = (long?)(m.Value as JObject)?["contextWindow"];
                        if (window != null) events.Add(new AgentEvent { Kind = AgentEventKind.Usage, Usage = new UsageInfo { ContextWindow = window } });
                        events.Add(AgentEvent.Of(AgentEventKind.Done));
                    }
                    break;
            }
            return events;
        }

        public IReadOnlyList<AgentEvent> AfterTurn(string sessionId) => new AgentEvent[0];

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
                    if (!(b is JObject)) continue;
                    var t = (string)b["type"];
                    var src = b["source"] as JObject;
                    if (t == "text") parts.Add(new ToolPart { Type = "text", Text = (string)b["text"] });
                    else if (t == "image")
                        parts.Add(new ToolPart { Type = "image", Mime = (string)src?["media_type"], Data = (string)src?["data"] });
                }
            return parts;
        }
    }
}
