using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace RhinoCC.Core.Agents
{
    /// <summary>One turn: starts the agent's CLI, feeds it the prompt and turns its output into events.</summary>
    public sealed class AgentRun
    {
        private readonly IAgentEngine _engine;
        private readonly ProcessStartInfo _psi;
        private readonly string _stdin;
        private Process _process;
        private volatile bool _cancelled;

        public event Action<AgentEvent> Event;

        public AgentRun(IAgentEngine engine, Invocation invocation, AgentSettings settings)
        {
            _engine = engine;
            _psi = ProcessHost.Build(invocation, settings);
            _stdin = invocation.Stdin ?? "";
        }

        private static string Trim(string s) => s != null && s.Length > 600 ? s.Substring(0, 600) + $"… ({s.Length} chars)" : s;

        public Task Start() => Task.Run(Run);

        public void Cancel()
        {
            _cancelled = true;
            try { _process?.Kill(); } catch { /* already gone */ }
        }

        private void Run()
        {
            var finished = false;
            void Raise(AgentEvent e)
            {
                if (e.Kind == AgentEventKind.Done || e.Kind == AgentEventKind.Failed)
                {
                    if (finished) return;
                    finished = true;
                }
                Event?.Invoke(e);
            }

            var stderr = new StringBuilder();
            var stderrDone = new System.Threading.ManualResetEventSlim();
            try
            {
                Log.Write($"start: {_psi.FileName} {Trim(_psi.Arguments)}");
                _process = new Process { StartInfo = _psi };
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) { stderrDone.Set(); return; }
                    Log.Write("stderr: " + Trim(e.Data));
                    lock (stderr) stderr.AppendLine(e.Data);
                };
                try { _process.Start(); }
                catch (Exception ex)
                {
                    Log.Write("could not start: " + ex);
                    Raise(AgentEvent.Of(AgentEventKind.Failed, $"Could not start {_engine.Program}: {ex.Message}. Set its path in the RhinoCC settings."));
                    return;
                }
                _process.BeginErrorReadLine();
                try
                {
                    _process.StandardInput.Write(_stdin);
                    _process.StandardInput.Close();
                }
                catch { /* the process may have exited already; its output says why */ }

                string line;
                while ((line = _process.StandardOutput.ReadLine()) != null)
                {
                    Log.Write("stdout: " + Trim(line));
                    foreach (var e in _engine.Parse(line)) Raise(e);
                }
                // Under Wine a Unix child can have no usable process handle; then there is no exit code, but its stderr still says why it ended
                var exitCode = "unknown";
                try
                {
                    _process.WaitForExit();
                    exitCode = _process.ExitCode.ToString();
                }
                catch (Exception ex) { Log.Write("no exit code: " + ex.Message); }
                stderrDone.Wait(3000);
                Log.Write("exited with code " + exitCode);

                if (_cancelled) Raise(AgentEvent.Of(AgentEventKind.Failed, "Stopped."));
                else if (!finished)
                {
                    string err;
                    lock (stderr) err = stderr.ToString().Trim();
                    if (err.Length > 1500) err = err.Substring(err.Length - 1500);
                    Raise(AgentEvent.Of(AgentEventKind.Failed, err.Length > 0 ? err : $"{_engine.Program} exited (code {exitCode}) without an answer and printed nothing on stderr."));
                }
            }
            catch (Exception ex)
            {
                Log.Write("run failed: " + ex);
                Raise(AgentEvent.Of(AgentEventKind.Failed, ex.Message));
            }
            finally
            {
                try { _process?.Dispose(); } catch { /* nothing to do */ }
            }
        }
    }
}
