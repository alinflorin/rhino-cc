using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RhinoCC.Core.Cli
{
    /// <summary>
    /// The command line that exists only inside Rhino. The agent gets one tool that runs it; the reference
    /// generated here goes into its instructions, so the manual cannot drift from the commands.
    /// </summary>
    public sealed class RhinoCli
    {
        private readonly Dictionary<string, CommandSpec> _commands = new Dictionary<string, CommandSpec>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>();

        /// <summary>Runs a command's handler; the plugin points this at Rhino's UI thread.</summary>
        public Func<Func<CliResult>, CliResult> Invoker { get; set; } = f => f();

        public IEnumerable<CommandSpec> Commands => _order.Select(n => _commands[n]);

        public void Register(CommandSpec spec)
        {
            if (_commands.ContainsKey(spec.Name)) throw new InvalidOperationException("duplicate command " + spec.Name);
            _commands[spec.Name] = spec;
            _order.Add(spec.Name);
        }

        public CliResult Execute(string line)
        {
            List<List<string>> statements;
            try
            {
                statements = ShellSplitter.Split(line);
            }
            catch (CliException e)
            {
                return CliResult.Fail("error: " + e.Message);
            }
            if (statements.Count == 0) return CliResult.Fail("error: empty command line. Run `help`.");

            var all = new CliResult();
            var text = new StringBuilder();
            foreach (var tokens in statements)
            {
                var r = RunOne(tokens);
                if (statements.Count > 1) text.Append("$ ").Append(tokens[0]).Append('\n');
                text.Append(r.Text);
                if (r.Text.Length > 0 && !r.Text.EndsWith("\n")) text.Append('\n');
                all.Images.AddRange(r.Images);
                if (r.IsError)
                {
                    all.IsError = true;
                    if (statements.Count > 1) text.Append("(stopped; the commands after this one were not run)\n");
                    break;
                }
            }
            all.Text = text.ToString().TrimEnd('\n');
            return all;
        }

        private CliResult RunOne(List<string> tokens)
        {
            if (tokens[0].Equals("rhino", StringComparison.OrdinalIgnoreCase) && tokens.Count > 1) tokens = tokens.Skip(1).ToList();
            var name = tokens[0];
            try
            {
                if (name.Equals("help", StringComparison.OrdinalIgnoreCase)) return Help(tokens.Skip(1).FirstOrDefault());
                if (!_commands.TryGetValue(name, out var spec))
                    return CliResult.Fail($"error: unknown command '{name}'. Run `help` for the list.");
                var args = Parse(spec, tokens.Skip(1).ToList());
                return Invoker(() => spec.Handler(args));
            }
            catch (CliException e)
            {
                return CliResult.Fail($"error: {e.Message}" + (_commands.ContainsKey(name) ? $"\nusage: {_commands[name].Usage}" : ""));
            }
            catch (Exception e)
            {
                return CliResult.Fail($"error: {name} failed: {Describe(e)}");
            }
        }

        /// <summary>The message, and for a null reference or a similar bug of ours, where it happened (the agent can pass that on).</summary>
        internal static string Describe(Exception e)
        {
            var inner = e.InnerException ?? e; // the UI-thread hop wraps the original
            if (!(inner is NullReferenceException || inner is InvalidCastException || inner is ArgumentException || inner is IndexOutOfRangeException || inner is InvalidOperationException))
                return e.Message;
            var frame = (inner.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal)) ?? "";
            return $"{inner.GetType().Name}: {inner.Message} {frame}".Trim();
        }

        private static CliArgs Parse(CommandSpec spec, List<string> tokens)
        {
            var args = new CliArgs();
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (!t.StartsWith("--", StringComparison.Ordinal) || t.Length == 2)
                {
                    args.Positional.Add(t);
                    continue;
                }
                var body = t.Substring(2);
                string inline = null;
                var eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    inline = body.Substring(eq + 1);
                    body = body.Substring(0, eq);
                }
                var opt = spec.Options.FirstOrDefault(o => o.Name.Equals(body, StringComparison.OrdinalIgnoreCase));
                if (opt == null)
                    throw new CliException($"unknown option --{body}" + (spec.Options.Count > 0 ? " (options: " + string.Join(", ", spec.Options.Select(o => "--" + o.Name)) + ")" : " (this command has no options)"));
                if (opt.IsFlag) args.SetOption(opt.Name, "true");
                else if (inline != null) args.SetOption(opt.Name, inline);
                else if (i + 1 < tokens.Count) args.SetOption(opt.Name, tokens[++i]);
                else throw new CliException($"--{opt.Name} needs a value ({opt.Value})");
            }
            return args;
        }

        private CliResult Help(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return CliResult.Ok(Reference(false));
            if (!_commands.TryGetValue(topic, out var spec)) return CliResult.Fail($"error: no command '{topic}'. Run `help`.");
            var sb = new StringBuilder();
            Describe(sb, spec);
            return CliResult.Ok(sb.ToString().TrimEnd('\n'));
        }

        private static void Describe(StringBuilder sb, CommandSpec c)
        {
            sb.Append(c.Usage).Append('\n').Append("    ").Append(c.Summary).Append('\n');
            foreach (var o in c.Options)
                sb.Append("    --").Append(o.Name).Append(o.IsFlag ? "" : " " + o.Value).Append("  ").Append(o.Description).Append('\n');
            foreach (var e in c.Examples) sb.Append("    e.g. ").Append(e).Append('\n');
        }

        /// <summary>The manual: every command with its options, grouped. <paramref name="full"/> adds the usage text.</summary>
        public string Reference(bool full = true)
        {
            var sb = new StringBuilder();
            sb.Append("COMMAND LINE: rhino\n");
            sb.Append("Give the command line without a leading `rhino`. Quote arguments with spaces ('...' or \"...\"). ")
              .Append("Join commands with && or newlines (it stops at the first error). Multi-line text goes in a here-document: `script <<EOF` ... `EOF` on its own line. ")
              .Append("Points are x,y,z (z optional). Object ids may be shortened to a unique prefix. Units are the document's. ")
              .Append(full ? "" : "`help COMMAND` shows every option of one command.")
              .Append("\n\n");
            foreach (var group in _order.Select(n => _commands[n]).GroupBy(c => c.Group))
            {
                sb.Append("## ").Append(group.Key).Append('\n');
                foreach (var c in group)
                {
                    if (full) Describe(sb, c);
                    else sb.Append(c.Usage).Append("  — ").Append(c.Summary).Append('\n');
                }
                sb.Append('\n');
            }
            sb.Append("help [COMMAND]\n    Lists the commands, or shows one.\n");
            return sb.ToString().TrimEnd('\n');
        }
    }
}
