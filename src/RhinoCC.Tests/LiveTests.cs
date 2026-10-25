using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RhinoCC.Core.Agents;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Mcp;
using Xunit;

namespace RhinoCC.Tests
{
    /// <summary>
    /// Runs the real CLIs against a fake Rhino. Off by default (it uses your Claude / Codex account):
    /// RHINOCC_LIVE=claude (or codex) dotnet test --filter Live
    /// </summary>
    public class LiveTests
    {
        private static async Task<List<AgentEvent>> Run(IAgentEngine engine)
        {
            var cli = new RhinoCli();
            cli.Register(new CommandSpec("status", "Test", "status", "Prints the fake state.", a => CliResult.Ok("Rhino 99, document: fake.3dm, objects: 7")));
            using (var server = new McpServer(cli, () => cli.Reference()))
            {
                server.Start(0);
                var turn = new Turn
                {
                    Prompt = "Run the `status` command through your exec_rhino tool, then tell me how many objects there are. Be brief.",
                    McpUrl = server.Url(true),
                    Instructions = cli.Reference(),
                    Model = engine.Id == "claude" ? "haiku" : null,
                };
                var run = new AgentRun(engine, engine.Prepare(turn), new AgentSettings());
                var events = new List<AgentEvent>();
                var done = new TaskCompletionSource<bool>();
                run.Event += e =>
                {
                    lock (events) events.Add(e);
                    if (e.Kind == AgentEventKind.Done || e.Kind == AgentEventKind.Failed) done.TrySetResult(true);
                };
                await run.Start();
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(3)));
                return events;
            }
        }

        private static async Task Check(IAgentEngine engine)
        {
            var events = await Run(engine);
            var text = string.Join("", events.Where(e => e.Kind == AgentEventKind.Delta).Select(e => e.Text));
            var failed = events.FirstOrDefault(e => e.Kind == AgentEventKind.Failed);
            Assert.True(failed == null, "failed: " + failed?.Text);
            Assert.Contains(events, e => e.Kind == AgentEventKind.ToolStart && e.Name == "exec_rhino" && e.Text == "status");
            Assert.Contains(events, e => e.Kind == AgentEventKind.ToolEnd && e.Parts.Any(p => p.Text.Contains("objects: 7")));
            Assert.Contains("7", text);
        }

        [Fact]
        public async Task ClaudeDrivesTheFakeCli()
        {
            if (Environment.GetEnvironmentVariable("RHINOCC_LIVE") != "claude") return;
            await Check(new ClaudeEngine());
        }

        [Fact]
        public async Task CodexDrivesTheFakeCli()
        {
            if (Environment.GetEnvironmentVariable("RHINOCC_LIVE") != "codex") return;
            await Check(new CodexEngine());
        }
    }
}
