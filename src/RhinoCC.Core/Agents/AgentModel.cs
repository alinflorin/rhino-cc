using System.Collections.Generic;
using System.IO;

namespace RhinoCC.Core.Agents
{
    /// <summary>A picture sent along with the user's message.</summary>
    public sealed class ImageAttachment
    {
        public string Name { get; set; }
        public string Mime { get; set; }
        public string Base64 { get; set; }
    }

    public enum AgentEventKind { Session, Delta, Thought, Break, ToolStart, ToolEnd, Usage, Warn, Failed, Done }

    public sealed class ToolPart
    {
        /// <summary>"text" or "image".</summary>
        public string Type { get; set; }
        public string Text { get; set; }
        public string Mime { get; set; }
        public string Data { get; set; }
    }

    /// <summary>A share of a time window of the account's plan that is used up.</summary>
    public sealed class LimitInfo
    {
        /// <summary>"5h", "weekly".</summary>
        public string Label { get; set; }
        /// <summary>0 to 100.</summary>
        public double Percent { get; set; }
        /// <summary>Unix seconds when it starts over.</summary>
        public long? ResetsAt { get; set; }
    }

    /// <summary>What the agent reported about the size of the conversation and the account's limits; any part may be missing.</summary>
    public sealed class UsageInfo
    {
        public long? ContextUsed { get; set; }
        public long? ContextWindow { get; set; }
        public List<LimitInfo> Limits { get; set; } = new List<LimitInfo>();
    }

    public sealed class AgentEvent
    {
        public UsageInfo Usage { get; set; }
        public AgentEventKind Kind { get; set; }
        /// <summary>Text for Delta / Thought / Warn / Failed, the session id for Session, the input for ToolStart.</summary>
        public string Text { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public List<ToolPart> Parts { get; set; }
        public bool Error { get; set; }

        public static AgentEvent Of(AgentEventKind kind, string text = null) => new AgentEvent { Kind = kind, Text = text };
    }

    public sealed class ModelInfo
    {
        public string Id { get; set; }
        public string Label { get; set; }
    }

    /// <summary>What the user asked for in one turn.</summary>
    public sealed class Turn
    {
        public string Prompt { get; set; }
        public string Model { get; set; }
        public string Effort { get; set; }
        /// <summary>The agent's own id for the conversation, to continue it.</summary>
        public string SessionId { get; set; }
        public string McpUrl { get; set; }
        public string Instructions { get; set; }
        /// <summary>Let the agent use its own tools (shell, files, web) next to ours.</summary>
        public bool AllTools { get; set; }
        public List<ImageAttachment> Images { get; set; } = new List<ImageAttachment>();
        /// <summary>
        /// A folder both the plugin and the CLI can reach, for what is too big for a command line or has to be a file.
        /// Local is how the plugin names it, Host how the CLI does (they differ under Wine: Z:\tmp\x and /tmp/x).
        /// </summary>
        public string ExchangeLocalDir { get; set; }
        public string ExchangeHostDir { get; set; }

        /// <summary>Writes a file into the exchange folder and returns its path as the CLI sees it; null when there is no folder.</summary>
        public string WriteExchange(string name, byte[] data)
        {
            if (string.IsNullOrEmpty(ExchangeLocalDir) || string.IsNullOrEmpty(ExchangeHostDir)) return null;
            Directory.CreateDirectory(ExchangeLocalDir);
            File.WriteAllBytes(Path.Combine(ExchangeLocalDir, name), data);
            var sep = ExchangeHostDir.Contains("/") ? "/" : "\\";
            return ExchangeHostDir.TrimEnd('/', '\\') + sep + name;
        }
    }

    /// <summary>A command line to run, before it is adapted to where the plugin runs.</summary>
    public sealed class Invocation
    {
        public string Program { get; set; }
        public List<string> Args { get; } = new List<string>();
        public string Stdin { get; set; }
    }

    public interface IAgentEngine
    {
        string Id { get; }
        string Label { get; }
        /// <summary>The executable's default name.</summary>
        string Program { get; }
        IReadOnlyList<ModelInfo> Models { get; }
        IReadOnlyList<string> Efforts { get; }
        /// <summary>The most instruction text the engine can take on its command line.</summary>
        int InstructionLimit { get; }
        Invocation Prepare(Turn turn);
        /// <summary>Reads one line of the agent's output.</summary>
        IReadOnlyList<AgentEvent> Parse(string line);
        /// <summary>Events for what the agent only records on disk once a turn is over (Codex's usage); none for most.</summary>
        IReadOnlyList<AgentEvent> AfterTurn(string sessionId);
    }

    /// <summary>Settings the launcher needs; the plugin fills this from its persistent settings.</summary>
    public sealed class AgentSettings
    {
        public string ClaudePath { get; set; } = "claude";
        public string CodexPath { get; set; } = "codex";
        /// <summary>Under Wine: the shell on the Linux host used to start the CLIs, so that its login PATH applies.</summary>
        public string HostShell { get; set; } = "/bin/sh";
    }
}
