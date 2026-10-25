using System.Linq;
using System.Text;

namespace RhinoCC.Core.Conversations
{
    /// <summary>
    /// A conversation as RTF for a read-only rich text box: replies are bold, thinking is grey italic, tool calls are blue
    /// and their output grey, failures red. Pictures from tools are not shown (the viewports have them).
    /// </summary>
    public static class RtfRenderer
    {
        private const int ToolOutputLimit = 700;

        // colour table indexes
        private const int Grey = 1, Blue = 2, Red = 3, Green = 4;

        public static string Render(Conversation c, string agentLabel, bool running)
        {
            var sb = new StringBuilder();
            sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}{\f1\fmodern Consolas;}}");
            sb.Append(@"{\colortbl ;\red120\green120\blue120;\red30\green90\blue170;\red190\green40\blue40;\red40\green130\blue70;}");
            sb.Append(@"\f0\fs19 ");
            for (var i = 0; i < c.Messages.Count; i++)
            {
                var m = c.Messages[i];
                var live = running && i == c.Messages.Count - 1 && m.Role == "assistant";
                var user = m.Role == "user";
                // who is talking
                Run(sb, (user ? "YOU" : agentLabel.ToUpperInvariant()), bold: true, color: user ? Green : Blue, size: 16);
                sb.Append(@"\par ");
                foreach (var b in m.Blocks)
                {
                    switch (b.Kind)
                    {
                        case "text":
                            // the user's own words stay plain; the agent's replies are the bold ones
                            Run(sb, (b.Text ?? "").TrimEnd(), bold: !user);
                            sb.Append(@"\par ");
                            break;
                        case "image":
                            Run(sb, "  [picture: " + b.Text + "]", color: Grey);
                            sb.Append(@"\par ");
                            break;
                        case "thought":
                            var first = (b.Text ?? "").Trim().Split('\n').FirstOrDefault() ?? "";
                            Run(sb, "  thinking: " + (first.Length > 140 ? first.Substring(0, 139) + "…" : first), italic: true, color: Grey);
                            sb.Append(@"\par ");
                            break;
                        case "tool":
                            Run(sb, "  ▸ " + b.ToolName + ": " + OneLine(b.Text) + (b.Done ? "" : "  (running)"), color: Blue, font: 1, size: 17);
                            sb.Append(@"\par ");
                            if (b.Output != null)
                            {
                                var text = string.Join("\n", b.Output.Where(p => p.Type == "text").Select(p => p.Text));
                                if (text.Length > ToolOutputLimit) text = text.Substring(0, ToolOutputLimit) + "…";
                                foreach (var line in text.Split('\n'))
                                {
                                    if (line.Length == 0) continue;
                                    Run(sb, "    " + (b.Error ? "✗ " : "") + line.TrimEnd('\r'), color: b.Error ? Red : Grey, font: 1, size: 17);
                                    sb.Append(@"\par ");
                                }
                            }
                            break;
                    }
                }
                if (live && m.Blocks.Count == 0)
                {
                    Run(sb, "  …", color: Grey);
                    sb.Append(@"\par ");
                }
                if (!string.IsNullOrEmpty(m.Failure))
                {
                    Run(sb, "⚠ " + m.Failure, bold: true, color: Red);
                    sb.Append(@"\par ");
                }
                sb.Append(@"\par ");
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void Run(StringBuilder sb, string text, bool bold = false, bool italic = false, int color = 0, int font = 0, int size = 19)
        {
            sb.Append('{').Append(@"\f").Append(font).Append(@"\fs").Append(size).Append(@"\cf").Append(color);
            if (bold) sb.Append(@"\b");
            if (italic) sb.Append(@"\i");
            sb.Append(' ');
            Escape(sb, text);
            sb.Append('}');
        }

        public static void Escape(StringBuilder sb, string text)
        {
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '{': sb.Append(@"\{"); break;
                    case '}': sb.Append(@"\}"); break;
                    case '\n': sb.Append(@"\line "); break;
                    case '\r': break;
                    case '\t': sb.Append(@"\tab "); break;
                    default:
                        if (ch < 32) break;
                        if (ch < 128) sb.Append(ch);
                        else sb.Append(@"\u").Append((short)ch).Append('?');
                        break;
                }
            }
        }

        private static string OneLine(string s)
        {
            s = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
            return s.Length > 300 ? s.Substring(0, 299) + "…" : s;
        }
    }
}
