using System.Linq;
using System.Text;

namespace RhinoCC.Core.Conversations
{
    /// <summary>Plain-text rendering of a conversation, for a read-only text box where the user can select and copy.</summary>
    public static class TranscriptRenderer
    {
        private const int ToolOutputLimit = 700;

        public static string Render(Conversation c, string agentLabel, bool running)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < c.Messages.Count; i++)
            {
                var m = c.Messages[i];
                var live = running && i == c.Messages.Count - 1 && m.Role == "assistant";
                sb.Append(m.Role == "user" ? "YOU" : agentLabel.ToUpperInvariant()).Append('\n');
                foreach (var b in m.Blocks)
                {
                    switch (b.Kind)
                    {
                        case "text":
                            sb.Append(b.Text.TrimEnd()).Append('\n');
                            break;
                        case "thought":
                            var first = (b.Text ?? "").Trim().Split('\n').FirstOrDefault() ?? "";
                            sb.Append("  … ").Append(first.Length > 140 ? first.Substring(0, 139) + "…" : first).Append('\n');
                            break;
                        case "tool":
                            sb.Append("  ▸ ").Append(b.ToolName).Append(": ").Append(OneLine(b.Text)).Append(b.Done ? "" : "  (running)").Append('\n');
                            if (b.Output != null)
                            {
                                var text = string.Join("\n", b.Output.Where(p => p.Type == "text").Select(p => p.Text));
                                var pictures = b.Output.Count(p => p.Type == "image");
                                if (text.Length > ToolOutputLimit) text = text.Substring(0, ToolOutputLimit) + "…";
                                foreach (var line in text.Split('\n')) if (line.Length > 0) sb.Append(b.Error ? "    ✗ " : "    ").Append(line).Append('\n');
                                if (pictures > 0) sb.Append("    [picture shown below]\n");
                            }
                            break;
                    }
                }
                if (live && m.Blocks.Count == 0) sb.Append("  …\n");
                if (!string.IsNullOrEmpty(m.Failure)) sb.Append("⚠ ").Append(m.Failure).Append('\n');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string OneLine(string s)
        {
            s = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
            return s.Length > 300 ? s.Substring(0, 299) + "…" : s;
        }
    }
}
