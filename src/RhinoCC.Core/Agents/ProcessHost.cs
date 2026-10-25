using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RhinoCC.Core.Agents
{
    /// <summary>Turns an <see cref="Invocation"/> into a process that works where the plugin runs, including Rhino under Wine.</summary>
    public static class ProcessHost
    {
        private static bool? _wine;

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>Wine exports <c>wine_get_version</c> from ntdll; real Windows does not.</summary>
        public static bool IsWine
        {
            get
            {
                if (_wine.HasValue) return _wine.Value;
                var wine = false;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    try { wine = GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version") != IntPtr.Zero; }
                    catch { /* not Windows after all */ }
                }
                _wine = wine;
                return wine;
            }
        }

        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>A Linux path as Wine sees it, through the Z: drive.</summary>
        public static string ToWinePath(string unixPath) => "Z:" + unixPath.Replace('/', '\\');

        public static ProcessStartInfo Build(Invocation inv, AgentSettings settings, bool? wine = null, bool? windows = null)
        {
            var useWine = wine ?? IsWine;
            var onWindows = windows ?? IsWindows;
            var program = inv.Program == "claude" ? settings.ClaudePath : inv.Program == "codex" ? settings.CodexPath : inv.Program;
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            var argv = new List<string>();
            if (useWine)
            {
                // a Unix program started through Wine; the shell gives it the host's login PATH, and a cwd that exists there
                psi.FileName = ToWinePath(settings.HostShell);
                argv.AddRange(new[] { "-lc", "cd /tmp && exec \"$0\" \"$@\"", program });
                argv.AddRange(inv.Args);
            }
            else if (onWindows && !System.IO.Path.HasExtension(program))
            {
                // npm installs the CLIs as .cmd shims, which only cmd.exe can start
                psi.FileName = "cmd.exe";
                argv.AddRange(new[] { "/c", program });
                argv.AddRange(inv.Args);
            }
            else
            {
                psi.FileName = program;
                argv.AddRange(inv.Args);
            }
            psi.Arguments = JoinArguments(argv);
            return psi;
        }

        /// <summary>Runs <c>PROGRAM --version</c> the way a real turn would start it, to tell the user whether the CLI can be found.</summary>
        public static KeyValuePair<bool, string> Probe(string program, AgentSettings settings, int timeoutMs = 20000)
        {
            var inv = new Invocation { Program = program };
            inv.Args.Add("--version");
            try
            {
                if (IsWine)
                {
                    var job = new WineJob(inv, settings);
                    var lines = new List<string>();
                    string code = null;
                    var task = System.Threading.Tasks.Task.Run(() => code = job.Run(l => { lock (lines) lines.Add(l); }, l => { lock (lines) lines.Add(l); }));
                    if (!task.Wait(timeoutMs))
                    {
                        job.Cancel();
                        return new KeyValuePair<bool, string>(false, "timed out");
                    }
                    string text;
                    lock (lines) text = string.Join("\n", lines).Trim();
                    var head = text.Split('\n')[0].Trim();
                    return code == "0" && head.Length > 0
                        ? new KeyValuePair<bool, string>(true, head)
                        : new KeyValuePair<bool, string>(false, head.Length > 0 ? head : "exit code " + (code ?? "unknown"));
                }
                using (var p = Process.Start(Build(inv, settings)))
                {
                    p.StandardInput.Close();
                    var output = p.StandardOutput.ReadToEndAsync();
                    var error = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { /* gone */ }
                        return new KeyValuePair<bool, string>(false, "timed out");
                    }
                    var text = (output.Result + "\n" + error.Result).Trim();
                    var first = text.Split('\n')[0].Trim();
                    return p.ExitCode == 0 && first.Length > 0
                        ? new KeyValuePair<bool, string>(true, first)
                        : new KeyValuePair<bool, string>(false, first.Length > 0 ? first : "exit code " + p.ExitCode);
                }
            }
            catch (Exception e)
            {
                return new KeyValuePair<bool, string>(false, e.Message);
            }
        }

        /// <summary>Quotes arguments by the Windows rules (what Wine and .NET Framework both parse back).</summary>
        public static string JoinArguments(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes).Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
