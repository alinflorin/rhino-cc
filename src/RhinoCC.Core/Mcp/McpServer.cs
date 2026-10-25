using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RhinoCC.Core.Cli;

namespace RhinoCC.Core.Mcp
{
    /// <summary>
    /// A small MCP server (Streamable HTTP, JSON replies) on the loopback interface. Two tools only: the
    /// manual and one that runs the fake CLI. Hand-rolled over TcpListener rather than HttpListener,
    /// because http.sys is not something to count on under Wine.
    /// </summary>
    public sealed class McpServer : IDisposable
    {
        public const string FullPath = "/mcp";
        /// <summary>For the built-in chat: the manual is already in the agent's instructions, so only the exec tool.</summary>
        public const string EmbeddedPath = "/mcp/embedded";

        private const string ServerInstructions =
            "Rhino 3D, driven through the `rhino` command line. IMPORTANT, BEFORE ANYTHING ELSE: call `read_instructions` " +
            "and read the whole result: it holds the user's instructions and the reference of every command. " +
            "`exec_rhino` refuses to run until you have.";

        private const string EmbeddedServerInstructions =
            "Rhino 3D, driven through the `rhino` command line (the `exec_rhino` tool). The complete manual is in your instructions.";

        private const string ExecDescription =
            "Run `rhino` commands, e.g. `status` or `objects --layer Default`. Give the command line without the leading `rhino`; " +
            "join several with && or newlines; multi-line text goes in a here-document (<<EOF). Returns text, and a picture for `shot`.";

        private readonly RhinoCli _cli;
        private readonly Func<string> _instructions;
        private TcpListener _listener;
        private CancellationTokenSource _stop;
        private volatile bool _read;

        public int Port { get; private set; }
        public bool IsRunning => _listener != null;

        public McpServer(RhinoCli cli, Func<string> instructions)
        {
            _cli = cli;
            _instructions = instructions;
        }

        public string Url(bool embedded) => $"http://127.0.0.1:{Port}{(embedded ? EmbeddedPath : FullPath)}";

        /// <summary>Starts listening; throws if the port is taken (the port is never chosen at random).</summary>
        public void Start(int port)
        {
            if (_listener != null) return;
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            _listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            _stop = new CancellationTokenSource();
            Task.Run(() => AcceptLoop(l, _stop.Token));
        }

        public void Stop()
        {
            _stop?.Cancel();
            try { _listener?.Stop(); } catch { /* already closed */ }
            _listener = null;
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 15000;
                    var stream = client.GetStream();
                    var req = ReadRequest(stream);
                    if (req == null) return;
                    var (status, body) = Handle(req);
                    var payload = Encoding.UTF8.GetBytes(body ?? "");
                    var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                    var h = Encoding.ASCII.GetBytes(head);
                    stream.Write(h, 0, h.Length);
                    stream.Write(payload, 0, payload.Length);
                }
                catch { /* a dropped connection is not our problem */ }
            }
        }

        private sealed class Request
        {
            public string Method, Path, Body;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Request ReadRequest(Stream s)
        {
            var buf = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (s.Read(one, 0, 1) <= 0) return null;
                buf.Add(one[0]);
                var n = buf.Count;
                if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n') break;
                if (n > 64 * 1024) return null;
            }
            var lines = Encoding.ASCII.GetString(buf.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new Request { Method = first[0], Path = first[1].Split('?')[0] };
            foreach (var l in lines)
            {
                var c = l.IndexOf(':');
                if (c > 0) req.Headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }
            var len = req.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n2) ? n2 : 0;
            if (len > 16 * 1024 * 1024) return null;
            var body = new byte[len];
            var got = 0;
            while (got < len)
            {
                var r = s.Read(body, got, len - got);
                if (r <= 0) return null;
                got += r;
            }
            req.Body = Encoding.UTF8.GetString(body);
            return req;
        }

        private (string status, string body) Handle(Request req)
        {
            // loopback only, and nothing a web page could send: browsers always add Origin to cross-site requests
            if (req.Headers.ContainsKey("Origin")) return ("403 Forbidden", "{\"error\":\"Origin not allowed\"}");
            if (req.Headers.TryGetValue("Host", out var host))
            {
                var h = host.Split(':')[0];
                if (h != "127.0.0.1" && h != "localhost" && h != "[::1]") return ("403 Forbidden", "{\"error\":\"Host not allowed\"}");
            }
            var embedded = req.Path == EmbeddedPath;
            if (req.Path != FullPath && !embedded) return ("404 Not Found", "{\"error\":\"not found\"}");
            if (req.Method == "DELETE") return ("200 OK", "{}");
            if (req.Method != "POST") return ("405 Method Not Allowed", "{\"error\":\"POST only\"}");

            JToken msg;
            try { msg = JToken.Parse(req.Body); }
            catch { return ("400 Bad Request", Error(null, -32700, "parse error")); }

            if (msg is JArray batch)
            {
                var replies = new JArray();
                foreach (var m in batch)
                {
                    var r = Dispatch((JObject)m, embedded);
                    if (r != null) replies.Add(r);
                }
                return replies.Count == 0 ? ("202 Accepted", "") : ("200 OK", replies.ToString(Newtonsoft.Json.Formatting.None));
            }
            var reply = Dispatch((JObject)msg, embedded);
            return reply == null ? ("202 Accepted", "") : ("200 OK", reply.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static string Error(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } }
                .ToString(Newtonsoft.Json.Formatting.None);

        private JObject Dispatch(JObject msg, bool embedded)
        {
            var method = (string)msg["method"];
            var id = msg["id"];
            if (id == null) return null; // a notification
            JObject result;
            switch (method)
            {
                case "initialize":
                    var asked = (string)msg["params"]?["protocolVersion"];
                    result = new JObject
                    {
                        ["protocolVersion"] = string.IsNullOrEmpty(asked) ? "2025-03-26" : asked,
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "rhino-cc", ["version"] = "0.1.0" },
                        ["instructions"] = embedded ? EmbeddedServerInstructions : ServerInstructions,
                    };
                    break;
                case "ping":
                    result = new JObject();
                    break;
                case "tools/list":
                    result = new JObject { ["tools"] = Tools(embedded) };
                    break;
                case "tools/call":
                    result = Call((string)msg["params"]?["name"], msg["params"]?["arguments"] as JObject, embedded);
                    break;
                default:
                    return JObject.Parse(Error(id, -32601, "method not found: " + method));
            }
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JArray Tools(bool embedded)
        {
            var tools = new JArray();
            if (!embedded)
                tools.Add(new JObject
                {
                    ["name"] = "read_instructions",
                    ["description"] = "Read this first, before any other tool: the user's instructions and the full reference of the `rhino` command line.",
                    ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject() },
                });
            tools.Add(new JObject
            {
                ["name"] = "exec_rhino",
                ["description"] = embedded ? ExecDescription : "Read the instructions first. " + ExecDescription,
                ["inputSchema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { ["args"] = new JObject { ["type"] = "string", ["description"] = "The command line, without the leading `rhino`." } },
                    ["required"] = new JArray("args"),
                },
            });
            return tools;
        }

        private JObject Call(string name, JObject args, bool embedded)
        {
            if (name == "read_instructions" && !embedded)
            {
                _read = true;
                return TextResult(_instructions(), false);
            }
            if (name == "exec_rhino")
            {
                if (!embedded && !_read)
                    return TextResult("You have not read the instructions yet. Call read_instructions now and read the whole result, then run this command again; nothing was done.", true);
                var line = (string)args?["args"];
                if (string.IsNullOrWhiteSpace(line)) return TextResult("error: `args` is empty. Run `help`.", true);
                Log.Write("mcp exec_rhino: " + line);
                var r = _cli.Execute(line);
                Log.Write($"mcp result{(r.IsError ? " (error)" : "")}: {(r.Text.Length > 300 ? r.Text.Substring(0, 300) + "…" : r.Text)}");
                var res = TextResult(r.Text.Length == 0 ? "(done)" : r.Text, r.IsError);
                foreach (var img in r.Images)
                    ((JArray)res["content"]).Add(new JObject { ["type"] = "image", ["data"] = img.Base64, ["mimeType"] = img.Mime });
                return res;
            }
            return TextResult("unknown tool: " + name, true);
        }

        private static JObject TextResult(string text, bool error) => new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = error,
        };
    }
}
