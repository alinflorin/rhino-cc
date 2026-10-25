using System.Diagnostics;
using RhinoCC.Core.Agents;
using Xunit;

namespace RhinoCC.Tests
{
    public class WineJobTests
    {
        [Theory]
        [InlineData("plain")]
        [InlineData("it's \"quoted\" $HOME `x`")]
        [InlineData("two\nlines & ; | <")]
        [InlineData("")]
        public void ShellQuote_survives_sh(string value)
        {
            if (System.Environment.OSVersion.Platform != System.PlatformID.Unix) return;
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("printf %s " + WineJob.ShellQuote(value));
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(value, output);
            }
        }

        [Fact]
        public void Script_runs_the_program_with_files_for_stdio()
        {
            var inv = new Invocation { Program = "claude", Stdin = "x" };
            inv.Args.AddRange(new[] { "-p", "--append-system-prompt", "a 'b'\nc" });
            var script = new WineJob(inv, new AgentSettings { ClaudePath = "/home/a/.local/bin/claude", HostShell = "/bin/sh" }).Script();
            Assert.StartsWith("#!/bin/sh\n", script);
            Assert.Contains("'/home/a/.local/bin/claude' '-p' '--append-system-prompt' 'a '\\''b'\\''\nc'", script);
            Assert.Contains("/in > ", script);
            Assert.Contains("/code", script);
        }
    }
}

namespace RhinoCC.Tests
{
    public class RtfRendererTests
    {
        [Fact]
        public void Replies_are_bold_thinking_italic_and_text_is_escaped()
        {
            var c = new RhinoCC.Core.Conversations.Conversation();
            c.AddUser("a {b} \\ c");
            var m = c.AddAssistant();
            m.Blocks.Add(new RhinoCC.Core.Conversations.Block { Kind = "thought", Text = "hmm", Done = true });
            m.Blocks.Add(new RhinoCC.Core.Conversations.Block { Kind = "text", Text = "Réponse", Done = true });
            var rtf = RhinoCC.Core.Conversations.RtfRenderer.Render(c, "Claude", false);
            Assert.StartsWith(@"{\rtf1", rtf);
            Assert.Contains(@"a \{b\} \\ c", rtf);
            Assert.Contains(@"\i   thinking: hmm", rtf);
            Assert.Contains(@"\b R\u233?ponse", rtf);
        }
    }
}
