using System.Collections.Generic;
using System.Text;

namespace RhinoCC.Core.Cli
{
    /// <summary>
    /// Splits a command line the way a small shell would: quotes, <c>&amp;&amp;</c> / <c>;</c> / newlines between
    /// commands, and here-documents (<c>&lt;&lt;EOF</c>) for multi-line text. No expansion of any kind.
    /// </summary>
    public static class ShellSplitter
    {
        public static List<List<string>> Split(string input)
        {
            var statements = new List<List<string>>();
            var tokens = new List<string>();
            var pending = new List<KeyValuePair<int, string>>();
            var sb = new StringBuilder();
            var inToken = false;
            input = (input ?? "").Replace("\r\n", "\n");
            var i = 0;

            void Flush()
            {
                if (!inToken) return;
                tokens.Add(sb.ToString());
                sb.Clear();
                inToken = false;
            }

            void End()
            {
                Flush();
                if (tokens.Count > 0) statements.Add(tokens);
                tokens = new List<string>();
            }

            while (i < input.Length)
            {
                var c = input[i];
                if (c == '\n')
                {
                    Flush();
                    i++;
                    foreach (var h in pending)
                    {
                        var body = new StringBuilder();
                        var first = true;
                        while (i <= input.Length)
                        {
                            var end = input.IndexOf('\n', i);
                            var line = end < 0 ? input.Substring(i) : input.Substring(i, end - i);
                            i = end < 0 ? input.Length + 1 : end + 1;
                            if (line == h.Value) break;
                            if (!first) body.Append('\n');
                            body.Append(line);
                            first = false;
                            if (end < 0) break;
                        }
                        tokens[h.Key] = body.ToString();
                    }
                    pending.Clear();
                    End();
                    continue;
                }
                if (c == ' ' || c == '\t')
                {
                    Flush();
                    i++;
                    continue;
                }
                if (c == ';' || (c == '&' && i + 1 < input.Length && input[i + 1] == '&'))
                {
                    End();
                    i += c == ';' ? 1 : 2;
                    continue;
                }
                if (!inToken && c == '<' && i + 1 < input.Length && input[i + 1] == '<')
                {
                    i += 2;
                    var delim = new StringBuilder();
                    var quote = i < input.Length && (input[i] == '\'' || input[i] == '"') ? input[i] : '\0';
                    if (quote != '\0') i++;
                    while (i < input.Length && input[i] != '\n' && (quote != '\0' ? input[i] != quote : input[i] != ' ' && input[i] != '\t'))
                        delim.Append(input[i++]);
                    if (quote != '\0' && i < input.Length && input[i] == quote) i++;
                    if (delim.Length == 0) throw new CliException("<< needs a delimiter, e.g. <<EOF");
                    pending.Add(new KeyValuePair<int, string>(tokens.Count, delim.ToString()));
                    tokens.Add("");
                    continue;
                }
                if (c == '\'')
                {
                    var end = input.IndexOf('\'', i + 1);
                    if (end < 0) throw new CliException("unterminated ' quote");
                    sb.Append(input, i + 1, end - i - 1);
                    inToken = true;
                    i = end + 1;
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    var closed = false;
                    while (i < input.Length)
                    {
                        if (input[i] == '\\' && i + 1 < input.Length && (input[i + 1] == '"' || input[i + 1] == '\\'))
                        {
                            sb.Append(input[i + 1]);
                            i += 2;
                        }
                        else if (input[i] == '"')
                        {
                            closed = true;
                            i++;
                            break;
                        }
                        else sb.Append(input[i++]);
                    }
                    if (!closed) throw new CliException("unterminated \" quote");
                    inToken = true;
                    continue;
                }
                sb.Append(c);
                inToken = true;
                i++;
            }
            if (pending.Count > 0)
            {
                foreach (var h in pending) tokens[h.Key] = "";
            }
            End();
            return statements;
        }
    }
}
