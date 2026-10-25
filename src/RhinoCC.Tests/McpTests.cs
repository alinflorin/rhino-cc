using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Mcp;
using Xunit;

namespace RhinoCC.Tests
{
    public class McpTests
    {
        private static McpServer Start()
        {
            var cli = new RhinoCli();
            cli.Register(new CommandSpec("echo", "Test", "echo WORDS...", "Prints.", a =>
            {
                var r = CliResult.Ok(string.Join(" ", a.Positional));
                if (a.Positional.Count > 0 && a.Positional[0] == "pic") r.Images.Add(new CliImage { Mime = "image/png", Base64 = "AAAA" });
                return r;
            }));
            var server = new McpServer(cli, () => "THE MANUAL");
            server.Start(0);
            return server;
        }

        private static (int status, JObject body) Post(McpServer s, string path, object payload, string extraHeaders = "")
        {
            using (var c = new TcpClient("127.0.0.1", s.Port))
            {
                var body = payload.ToString();
                var req = $"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{s.Port}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{extraHeaders}\r\n{body}";
                var bytes = Encoding.UTF8.GetBytes(req);
                var stream = c.GetStream();
                stream.Write(bytes, 0, bytes.Length);
                var reader = new StreamReader(stream, Encoding.UTF8);
                var all = reader.ReadToEnd();
                var status = int.Parse(all.Split(' ')[1]);
                var split = all.IndexOf("\r\n\r\n") + 4;
                var text = all.Substring(split);
                return (status, text.Length == 0 ? null : JObject.Parse(text));
            }
        }

        private static JObject Rpc(int id, string method, object p = null) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p == null ? null : JToken.FromObject(p) };

        [Fact]
        public void FullEndpointHasBothToolsAndGatesExec()
        {
            using (var s = Start())
            {
                var init = Post(s, "/mcp", Rpc(1, "initialize", new { protocolVersion = "2025-06-18" }));
                Assert.Equal(200, init.status);
                Assert.Equal("2025-06-18", (string)init.body["result"]["protocolVersion"]);

                var tools = Post(s, "/mcp", Rpc(2, "tools/list"));
                Assert.Equal(new[] { "read_instructions", "exec_rhino" }, tools.body["result"]["tools"].Select(t => (string)t["name"]));

                var refused = Post(s, "/mcp", Rpc(3, "tools/call", new { name = "exec_rhino", arguments = new { args = "echo hi" } }));
                Assert.True((bool)refused.body["result"]["isError"]);

                var manual = Post(s, "/mcp", Rpc(4, "tools/call", new { name = "read_instructions" }));
                Assert.Equal("THE MANUAL", (string)manual.body["result"]["content"][0]["text"]);

                var ok = Post(s, "/mcp", Rpc(5, "tools/call", new { name = "exec_rhino", arguments = new { args = "echo hi there" } }));
                Assert.False((bool)ok.body["result"]["isError"]);
                Assert.Equal("hi there", (string)ok.body["result"]["content"][0]["text"]);
            }
        }

        [Fact]
        public void EmbeddedEndpointHasOnlyExecAndNoGate()
        {
            using (var s = Start())
            {
                var tools = Post(s, McpServer.EmbeddedPath, Rpc(1, "tools/list"));
                Assert.Equal(new[] { "exec_rhino" }, tools.body["result"]["tools"].Select(t => (string)t["name"]));
                var r = Post(s, McpServer.EmbeddedPath, Rpc(2, "tools/call", new { name = "exec_rhino", arguments = new { args = "echo pic" } }));
                Assert.Equal("image", (string)r.body["result"]["content"][1]["type"]);
                Assert.Equal("image/png", (string)r.body["result"]["content"][1]["mimeType"]);
            }
        }

        [Fact]
        public void NotificationsAreAcceptedWithoutABody()
        {
            using (var s = Start())
            {
                var r = Post(s, "/mcp", new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
                Assert.Equal(202, r.status);
            }
        }

        [Fact]
        public void RefusesBrowsersAndOtherHosts()
        {
            using (var s = Start())
            {
                Assert.Equal(403, Post(s, "/mcp", Rpc(1, "ping"), "Origin: http://evil.example\r\n").status);
                Assert.Equal(404, Post(s, "/other", Rpc(1, "ping")).status);
            }
        }
    }
}
