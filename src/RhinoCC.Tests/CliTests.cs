using System.Linq;
using RhinoCC.Core.Cli;
using Xunit;

namespace RhinoCC.Tests
{
    public class ShellSplitterTests
    {
        [Fact]
        public void SplitsQuotesAndOperators()
        {
            var s = ShellSplitter.Split("add box 1 2 3 --name 'my box' && layer-new \"A \\\"b\\\"\"; status");
            Assert.Equal(3, s.Count);
            Assert.Equal(new[] { "add", "box", "1", "2", "3", "--name", "my box" }, s[0]);
            Assert.Equal(new[] { "layer-new", "A \"b\"" }, s[1]);
            Assert.Equal(new[] { "status" }, s[2]);
        }

        [Fact]
        public void KeepsEmptyQuotedArguments()
        {
            Assert.Equal(new[] { "set", "", "x" }, ShellSplitter.Split("set '' x")[0]);
        }

        [Fact]
        public void HereDocumentBecomesOneArgument()
        {
            var s = ShellSplitter.Split("script <<EOF\nimport rhinoscriptsyntax as rs\nprint 'hi && there'\nEOF\nstatus");
            Assert.Equal(2, s.Count);
            Assert.Equal(new[] { "script", "import rhinoscriptsyntax as rs\nprint 'hi && there'" }, s[0]);
            Assert.Equal(new[] { "status" }, s[1]);
        }

        [Fact]
        public void HereDocumentWithoutTerminatorTakesTheRest()
        {
            var s = ShellSplitter.Split("script <<EOF\na\nb");
            Assert.Equal("a\nb", s[0][1]);
        }

        [Fact]
        public void UnterminatedQuoteIsAnError()
        {
            Assert.Throws<CliException>(() => ShellSplitter.Split("echo 'oops"));
        }

        [Fact]
        public void WindowsPathsSurvive()
        {
            Assert.Equal(@"C:\temp\a.3dm", ShellSplitter.Split(@"open C:\temp\a.3dm")[0][1]);
        }
    }

    public class RhinoCliTests
    {
        private static RhinoCli Make()
        {
            var cli = new RhinoCli();
            cli.Register(new CommandSpec("echo", "Test", "echo WORDS... [--up]", "Prints words.",
                a => CliResult.Ok(string.Join(" ", a.Positional.Select(p => a.Has("up") ? p.ToUpperInvariant() : p))))
                .Flag("up", "Upper-case."));
            cli.Register(new CommandSpec("fail", "Test", "fail", "Always fails.", a => CliResult.Fail("nope")));
            cli.Register(new CommandSpec("num", "Test", "num N [--by N]", "Adds.",
                a => CliResult.Ok((a.PosNumber(0, "N") + (a.OptNumber("by") ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Opt("by", "N", "Offset."));
            return cli;
        }

        [Fact]
        public void RunsAndParsesOptions()
        {
            var r = Make().Execute("echo a b --up");
            Assert.False(r.IsError);
            Assert.Equal("A B", r.Text);
            Assert.Equal("3.5", Make().Execute("num 1.5 --by=2").Text);
            Assert.Equal("-4", Make().Execute("num -4").Text);
        }

        [Fact]
        public void AcceptsAnOptionalRhinoPrefix()
        {
            Assert.Equal("x", Make().Execute("rhino echo x").Text);
        }

        [Fact]
        public void StopsAtTheFirstError()
        {
            var r = Make().Execute("echo one && fail && echo two");
            Assert.True(r.IsError);
            Assert.Contains("$ echo", r.Text);
            Assert.Contains("nope", r.Text);
            Assert.DoesNotContain("two", r.Text.Replace("(stopped; the commands after this one were not run)", ""));
        }

        [Fact]
        public void ExplainsMistakes()
        {
            Assert.Contains("unknown command", Make().Execute("nothing").Text);
            Assert.Contains("unknown option --zip", Make().Execute("echo --zip").Text);
            Assert.Contains("is not a number", Make().Execute("num abc").Text);
            Assert.Contains("usage: num N", Make().Execute("num").Text);
            Assert.Contains("missing", Make().Execute("num").Text);
        }

        [Fact]
        public void HandlersRunThroughTheInvoker()
        {
            var cli = Make();
            var calls = 0;
            cli.Invoker = f => { calls++; return f(); };
            cli.Execute("echo a && echo b");
            Assert.Equal(2, calls);
        }

        [Fact]
        public void ReferenceListsEveryCommand()
        {
            var text = Make().Reference();
            Assert.Contains("echo WORDS... [--up]", text);
            Assert.Contains("--by N  Offset.", text);
            Assert.Contains("## Test", text);
            Assert.Contains("help [COMMAND]", text);
            Assert.Contains("fail", Make().Execute("help").Text);
            Assert.Contains("Prints words.", Make().Execute("help echo").Text);
        }

        [Fact]
        public void CompactReferenceIsOneLinePerCommand()
        {
            var compact = Make().Reference(false);
            Assert.Contains("echo WORDS... [--up]  — Prints words.", compact);
            Assert.DoesNotContain("--by N  Offset.", compact);
            Assert.Contains("`help COMMAND` shows every option", compact);
        }
    }
}
