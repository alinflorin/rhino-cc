using System.Collections.Generic;

namespace RhinoCC.Core.Agents
{
    public enum AgentEventKind { Session, Delta, Thought, Break, ToolStart, ToolEnd, Warn, Failed, Done }

    public sealed class ToolPart
    {
        /// <summary>"text" or "image".</summary>
        public string Type { get; set; }
        public string Text { get; set; }
        public string Mime { get; set; }
        public string Data { get; set; }
    }

    public sealed class AgentEvent
    {
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
        Invocation Prepare(Turn turn);
        /// <summary>Reads one line of the agent's output.</summary>
        IReadOnlyList<AgentEvent> Parse(string line);
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
