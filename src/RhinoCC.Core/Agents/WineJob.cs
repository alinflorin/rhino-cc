using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RhinoCC.Core.Agents
{
    /// <summary>
    /// Runs a Linux program from Rhino under Wine. Wine's CreateProcess cannot start a Unix executable with piped
    /// stdio (the process comes back with no handle and no output), but <c>start /unix</c> can start one. That
    /// detaches it, so the program runs from a small script that reads its stdin from a file and writes stdout, stderr
    /// and its exit code to files, which are followed from this side. The files live in the exchange folder for one turn.
    /// </summary>
    public sealed class WineJob : IHostJob
    {
        private readonly Invocation _inv;
        private readonly AgentSettings _settings;
        private readonly string _unixDir;
        private readonly string _dir;
        private volatile bool _cancelled;

        public WineJob(Invocation inv, AgentSettings settings)
        {
            _inv = inv;
            _settings = settings;
            _unixDir = "/tmp/rhinocc/job-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _dir = ProcessHost.ToWinePath(_unixDir);
        }

        /// <summary>Single-quotes a value for sh.</summary>
        public static string ShellQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        private string Win(string name) => Path.Combine(_dir, name);
        private string Unix(string name) => _unixDir + "/" + name;

        public string Script()
        {
            var program = _inv.Program == "claude" ? _settings.ClaudePath : _inv.Program == "codex" ? _settings.CodexPath : _inv.Program;
            var sb = new StringBuilder();
            sb.Append("#!/bin/sh\ncd /tmp\n");
            // the inner shell records its pid, then becomes the program, so that pid is the one to stop
            sb.Append(ShellQuote(_settings.HostShell)).Append(" -lc ")
              .Append(ShellQuote("echo $$ > " + Unix("pid") + "; exec \"$0\" \"$@\""))
              .Append(' ').Append(ShellQuote(program));
            foreach (var a in _inv.Args) sb.Append(' ').Append(ShellQuote(a));
            sb.Append(" < ").Append(Unix("in")).Append(" > ").Append(Unix("out")).Append(" 2> ").Append(Unix("err")).Append('\n');
            sb.Append("echo $? > ").Append(Unix("code.tmp")).Append(" && mv ").Append(Unix("code.tmp")).Append(' ').Append(Unix("code")).Append('\n');
            return sb.ToString();
        }

        private static void WriteText(string path, string text) => File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

        private void Launch(string unixScript)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start /unix " + _settings.HostShell + " " + unixScript,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi)) p.WaitForExit(10000);
        }

        public void Cancel()
        {
            _cancelled = true;
            try { Launch(Unix("kill.sh")); }
            catch (Exception e) { Log.Write("cancel failed: " + e.Message); }
        }

        /// <summary>Runs to the end, calling back for each output line. Returns the exit code, or null if it could not be read.</summary>
        public string Run(Action<string> onOut, Action<string> onErr)
        {
            Directory.CreateDirectory(_dir);
            try
            {
                WriteText(Win("in"), _inv.Stdin ?? "");
                WriteText(Win("run.sh"), Script());
                WriteText(Win("kill.sh"), "#!/bin/sh\nkill $(cat " + Unix("pid") + ") 2>/dev/null\n");
                Log.Write($"job {_unixDir}: {Script().Length} chars of script");
                Launch(Unix("run.sh"));

                var started = DateTime.UtcNow;
                var cancelledAt = DateTime.MinValue;
                var o = new Tailer(Win("out"), onOut);
                var e = new Tailer(Win("err"), onErr);
                while (true)
                {
                    o.Drain();
                    e.Drain();
                    if (File.Exists(Win("code"))) break;
                    if (_cancelled && cancelledAt == DateTime.MinValue) cancelledAt = DateTime.UtcNow;
                    if (cancelledAt != DateTime.MinValue && DateTime.UtcNow - cancelledAt > TimeSpan.FromSeconds(5)) break;
                    // nothing ever started: the launcher itself failed
                    if (!File.Exists(Win("pid")) && !File.Exists(Win("code")) && DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
                        throw new InvalidOperationException("The agent did not start within 30 seconds (Wine's `start /unix` did nothing). Is " + _settings.HostShell + " available there?");
                    Thread.Sleep(40);
                }
                o.Drain();
                e.Drain();
                o.Flush();
                e.Flush();
                try { return File.ReadAllText(Win("code")).Trim(); }
                catch { return null; }
            }
            finally
            {
                try { Directory.Delete(_dir, true); } catch { /* the next turn's cleanup gets it */ }
            }
        }

        /// <summary>Follows a file that another process appends to, line by line.</summary>
        private sealed class Tailer
        {
            private readonly string _path;
            private readonly Action<string> _line;
            private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
            private readonly StringBuilder _partial = new StringBuilder();
            private long _pos;

            public Tailer(string path, Action<string> line) { _path = path; _line = line; }

            public void Drain()
            {
                if (!File.Exists(_path)) return;
                using (var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (f.Length <= _pos) return;
                    f.Seek(_pos, SeekOrigin.Begin);
                    var buf = new byte[64 * 1024];
                    int n;
                    var chars = new char[buf.Length + 4];
                    while ((n = f.Read(buf, 0, buf.Length)) > 0)
                    {
                        _pos += n;
                        var c = _decoder.GetChars(buf, 0, n, chars, 0);
                        for (var i = 0; i < c; i++)
                        {
                            if (chars[i] == '\n')
                            {
                                var s = _partial.ToString().TrimEnd('\r');
                                _partial.Clear();
                                _line(s);
                            }
                            else _partial.Append(chars[i]);
                        }
                    }
                }
            }

            public void Flush()
            {
                if (_partial.Length == 0) return;
                var s = _partial.ToString();
                _partial.Clear();
                _line(s);
            }
        }
    }
}
