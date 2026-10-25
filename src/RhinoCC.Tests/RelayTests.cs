using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using RhinoCC.Core.Agents;
using Xunit;

namespace RhinoCC.Tests
{
    /// <summary>Runs the real scripts/rhinocc-relay.py (with a fake `claude`) and talks to it with RelayJob.</summary>
    public class RelayTests : IDisposable
    {
        private readonly string _home = Path.Combine(Path.GetTempPath(), "rhinocc-relay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly Process _relay;
        private readonly RelayConfig _config;
        private readonly bool _can = !OperatingSystem.IsWindows() && File.Exists(ScriptPath());

        private static string ScriptPath()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var f = Path.Combine(dir.FullName, "scripts", "rhinocc-relay.py");
                if (File.Exists(f)) return f;
            }
            return "";
        }

        public RelayTests()
        {
            if (!_can) return;
            int port;
            using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
            Directory.CreateDirectory(Path.Combine(_home, ".config", "rhinocc"));
            Directory.CreateDirectory(Path.Combine(_home, ".local", "bin"));
            File.WriteAllText(Path.Combine(_home, ".config", "rhinocc", "relay.json"), "{\"port\": " + port + ", \"token\": \"sekret\"}");
            var fake = Path.Combine(_home, ".local", "bin", "claude");
            File.WriteAllText(fake, "#!/bin/sh\necho \"args: $*\"\ncat\necho oops >&2\n[ \"$1\" = slow ] && { echo started; sleep 30; }\nexit 3\n");
            File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
            var psi = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true };
            psi.ArgumentList.Add(ScriptPath());
            psi.Environment["HOME"] = _home;
            _relay = Process.Start(psi);
            _relay.StandardOutput.ReadLine(); // "listening"
            _config = new RelayConfig { Port = port, Token = "sekret" };
        }

        public void Dispose()
        {
            try { _relay?.Kill(); } catch { }
            try { Directory.Delete(_home, true); } catch { }
        }

        private static Invocation Inv(string stdin, params string[] args)
        {
            var inv = new Invocation { Program = "claude", Stdin = stdin };
            inv.Args.AddRange(args);
            return inv;
        }

        [Fact]
        public void Runs_the_program_with_its_arguments_and_stdin_and_reports_stdout_stderr_and_the_exit_code()
        {
            if (!_can) return;
            var outLines = new List<string>();
            var errLines = new List<string>();
            var code = new RelayJob(Inv("héllo\nworld", "a b", "c'd"), new AgentSettings(), _config).Run(outLines.Add, errLines.Add);
            Assert.Equal("3", code);
            Assert.Equal(new[] { "args: a b c'd", "héllo", "world" }, outLines);
            Assert.Equal(new[] { "oops" }, errLines);
        }

        [Fact]
        public void A_wrong_token_is_refused()
        {
            if (!_can) return;
            var bad = new RelayConfig { Port = _config.Port, Token = "nope" };
            var e = Assert.Throws<InvalidOperationException>(() => new RelayJob(Inv(""), new AgentSettings(), bad).Run(l => { }, l => { }));
            Assert.Contains("bad token", e.Message);
        }

        [Fact]
        public void Only_claude_and_codex_can_be_started()
        {
            if (!_can) return;
            var inv = new Invocation { Program = "bash", Stdin = "" };
            var e = Assert.Throws<InvalidOperationException>(() => new RelayJob(inv, new AgentSettings(), _config).Run(l => { }, l => { }));
            Assert.Contains("only claude and codex", e.Message);
        }

        [Fact]
        public void Cancel_stops_the_program()
        {
            if (!_can) return;
            var job = new RelayJob(Inv("", "slow"), new AgentSettings(), _config);
            var started = new System.Threading.ManualResetEventSlim();
            var task = System.Threading.Tasks.Task.Run(() => job.Run(l => { if (l == "started") started.Set(); }, l => { }));
            Assert.True(started.Wait(10000));
            var watch = Stopwatch.StartNew();
            job.Cancel();
            Assert.True(task.Wait(10000));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Null(task.Result);
        }

        [Fact]
        public void A_relay_config_needs_a_token()
        {
            Assert.Null(RelayConfig.Parse("{\"port\": 1}"));
            Assert.Null(RelayConfig.Parse("not json"));
            var c = RelayConfig.Parse("{\"port\": 5, \"token\": \"t\"}");
            Assert.Equal(5, c.Port);
        }
    }
}
