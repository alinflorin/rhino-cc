using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Only suggestions: the model box accepts any name, and Codex's real list changes often (`codex debug models`). Blank means "its default".
        public IReadOnlyList<ModelInfo> Models { get; } = new[]
        {
            new ModelInfo { Id = "gpt-6.1-sol", Label = "GPT-6.1 Sol" },
            new ModelInfo { Id = "gpt-6-astra", Label = "GPT-6 Astra" },
            new ModelInfo { Id = "gpt-6-sol", Label = "GPT-6 Sol" },
            new ModelInfo { Id = "gpt-6-luna", Label = "GPT-6 Luna" },
            new ModelInfo { Id = "gpt-5.6-sol", Label = "GPT-5.6 Sol" },
            new ModelInfo { Id = "gpt-5.6-terra", Label = "GPT-5.6 Terra" },
            new ModelInfo { Id = "gpt-5.6-luna", Label = "GPT-5.6 Luna" },
        };

        public IReadOnlyList<string> Efforts { get; } = new[] { "low", "medium", "high", "xhigh", "max", "ultra" };

        // travels as a -c override, so it shares the command line (Windows: 32K characters) with everything else
        public int InstructionLimit => 29000;

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
            inv.Args.AddRange(new[] { "--json", "--skip-git-repo-check" });
            // pictures are handed over as files
            for (var i = 0; i < turn.Images.Count; i++)
            {
                var img = turn.Images[i];
                var ext = img.Mime != null && img.Mime.Contains("/") ? img.Mime.Substring(img.Mime.IndexOf('/') + 1) : "png";
                var file = turn.WriteExchange($"image-{i}.{ext}", System.Convert.FromBase64String(img.Base64));
                if (file == null) throw new System.InvalidOperationException("no folder to hand pictures to Codex");
                inv.Args.AddRange(new[] { "-i", file });
            }
            inv.Args.Add("-");
            return inv;
        }

        public IReadOnlyList<AgentEvent> Parse(string line)
        {
            var events = new List<AgentEvent>();
            JObject v;
            try { v = JObject.Parse(line); }
            catch { return events; }

            var item = v["item"] as JObject; // can be null or a plain value in other events
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
                        var parts = Parts((item["result"] as JObject)?["content"]);
                        var message = item["error"] is JObject eo ? (string)eo["message"] : item["error"]?.Type == JTokenType.String ? (string)item["error"] : null;
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
                    events.Add(AgentEvent.Of(AgentEventKind.Failed, (v["error"] as JObject)?["message"]?.ToString() ?? "the agent failed"));
                    break;
                case "error":
                    if (v["message"] != null) events.Add(AgentEvent.Of(AgentEventKind.Warn, (string)v["message"]));
                    break;
            }
            return events;
        }

        /// <summary>Codex keeps the size of the conversation and the account's limits only in its record of the session.</summary>
        public IReadOnlyList<AgentEvent> AfterTurn(string sessionId)
        {
            var events = new List<AgentEvent>();
            try
            {
                var file = string.IsNullOrEmpty(sessionId) ? null : FindRollout(Path.Combine(CodexHome(), "sessions"), sessionId);
                if (file == null) return events;
                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, System.Text.Encoding.UTF8)) text = r.ReadToEnd();
                var usage = ReadUsage(text);
                if (usage != null) events.Add(new AgentEvent { Kind = AgentEventKind.Usage, Usage = usage });
            }
            catch (System.Exception e) { Log.Write("codex usage: " + e.Message); }
            return events;
        }

        private static string CodexHome()
        {
            var home = System.Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(home)) return ProcessHost.IsWine && home.StartsWith("/") ? ProcessHost.ToWinePath(home) : home;
            // under Wine HOME is the Linux home, which the Z: drive reaches
            var unix = System.Environment.GetEnvironmentVariable("HOME");
            if (ProcessHost.IsWine && !string.IsNullOrEmpty(unix) && unix.StartsWith("/")) return ProcessHost.ToWinePath(unix + "/.codex");
            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".codex");
        }

        /// <summary>Codex's record of a session is <c>rollout-&lt;time&gt;-&lt;session&gt;.jsonl</c> in dated folders.</summary>
        internal static string FindRollout(string dir, string session)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.GetFiles(dir, "rollout-*" + session + ".jsonl")) return f;
            foreach (var d in Directory.GetDirectories(dir).OrderByDescending(x => x))
            {
                var found = FindRollout(d, session);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>The last <c>token_count</c> record: the size of the conversation and the account's limits.</summary>
        public static UsageInfo ReadUsage(string record)
        {
            foreach (var line in record.Split('\n').Reverse())
            {
                if (!line.Contains("\"token_count\"")) continue;
                JObject v;
                try { v = JObject.Parse(line); }
                catch { continue; }
                var p = v["payload"];
                if (!(p?["info"] is JObject info)) continue;
                var usage = new UsageInfo
                {
                    ContextUsed = (long?)info["last_token_usage"]?["total_tokens"],
                    ContextWindow = (long?)info["model_context_window"],
                };
                foreach (var w in new[] { p["rate_limits"]?["primary"], p["rate_limits"]?["secondary"] })
                {
                    if (w?["used_percent"] == null) continue;
                    var minutes = (long?)w["window_minutes"] ?? 0;
                    usage.Limits.Add(new LimitInfo
                    {
                        Label = minutes == 300 ? "5h" : minutes == 10080 ? "weekly" : minutes > 0 && minutes % 60 == 0 ? minutes / 60 + "h" : minutes + "m",
                        Percent = (double)w["used_percent"],
                        ResetsAt = (long?)w["resets_at"],
                    });
                }
                return usage;
            }
            return null;
        }

        private static List<ToolPart> Parts(JToken content)
        {
            var parts = new List<ToolPart>();
            if (content is JArray arr)
                foreach (var b in arr)
                {
                    if (!(b is JObject)) continue;
                    var t = (string)b["type"];
                    if (t == "text") parts.Add(new ToolPart { Type = "text", Text = (string)b["text"] });
                    else if (t == "image") parts.Add(new ToolPart { Type = "image", Mime = (string)b["mimeType"], Data = (string)b["data"] });
                }
            return parts;
        }
    }
}
