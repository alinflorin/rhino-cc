using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RhinoCC.Core.Agents
{
    /// <summary>A Linux program run for Rhino under Wine, and followed until it ends.</summary>
    public interface IHostJob
    {
        /// <summary>Runs to the end, calling back for each output line. Returns the exit code, or null if it could not be read.</summary>
        string Run(Action<string> onOut, Action<string> onErr);
        void Cancel();
    }

    /// <summary>
    /// Runs the program through the relay service of scripts/install-relay.sh, which listens on the loopback and starts
    /// claude / codex with ordinary pipes. Nothing touches the disk, and output arrives as it is written.
    /// </summary>
    public sealed class RelayJob : IHostJob
    {
        private readonly Invocation _inv;
        private readonly AgentSettings _settings;
        private readonly RelayConfig _config;
        private TcpClient _client;
        private volatile bool _cancelled;

        public RelayJob(Invocation inv, AgentSettings settings, RelayConfig config)
        {
            _inv = inv;
            _settings = settings;
            _config = config;
        }

        /// <summary>The relay when it is installed and answering, otherwise the <c>start /unix</c> way.</summary>
        public static IHostJob Create(Invocation inv, AgentSettings settings)
        {
            var config = RelayConfig.Find();
            if (config != null && config.Reachable()) return new RelayJob(inv, settings, config);
            Log.Write(config == null ? "no relay installed; starting the program through `start /unix`" : "the relay is installed but not answering; starting the program through `start /unix`");
            return new WineJob(inv, settings);
        }

        /// <summary>The first line the relay is sent: who we are, what to run, and how many bytes of stdin follow.</summary>
        public static byte[] Request(Invocation inv, AgentSettings settings, string token)
        {
            var program = inv.Program == "claude" ? settings.ClaudePath : inv.Program == "codex" ? settings.CodexPath : inv.Program;
            var stdin = new UTF8Encoding(false).GetBytes(inv.Stdin ?? "");
            var header = new JObject
            {
                ["token"] = token,
                ["program"] = program,
                ["args"] = new JArray(inv.Args),
                ["stdin"] = stdin.Length,
            }.ToString(Newtonsoft.Json.Formatting.None);
            var head = new UTF8Encoding(false).GetBytes(header + "\n");
            var all = new byte[head.Length + stdin.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(stdin, 0, all, head.Length, stdin.Length);
            return all;
        }

        public void Cancel()
        {
            _cancelled = true;
            // the relay stops the program when the connection goes away
            try { _client?.Close(); }
            catch { /* already closed */ }
        }

        public string Run(Action<string> onOut, Action<string> onErr)
        {
            _client = new TcpClient();
            try
            {
                _client.Connect("127.0.0.1", _config.Port);
                if (_cancelled) return null;
                var stream = _client.GetStream();
                var request = Request(_inv, _settings, _config.Token);
                stream.Write(request, 0, request.Length);
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                var first = reader.ReadLine();
                if (first != "OK") throw new InvalidOperationException("the relay refused: " + (first == null ? "it closed the connection" : first.StartsWith("ERR ") ? first.Substring(4) : first));
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("O ")) onOut(line.Substring(2));
                    else if (line.StartsWith("E ")) onErr(line.Substring(2));
                    else if (line.StartsWith("X ")) return line.Substring(2).Trim();
                }
                return null;
            }
            catch (Exception) when (_cancelled)
            {
                return null; // closed on purpose
            }
            finally
            {
                try { _client.Close(); }
                catch { /* gone */ }
            }
        }
    }

    /// <summary>Where the relay listens and the secret it wants, from <c>~/.config/rhinocc/relay.json</c> on the Linux side.</summary>
    public sealed class RelayConfig
    {
        public int Port { get; set; }
        public string Token { get; set; }

        public static RelayConfig Parse(string json)
        {
            try
            {
                var v = JObject.Parse(json);
                var token = (string)v["token"];
                if (string.IsNullOrEmpty(token)) return null;
                return new RelayConfig { Port = (int?)v["port"] ?? 45601, Token = token };
            }
            catch { return null; }
        }

        public static RelayConfig Find()
        {
            if (!ProcessHost.IsWine) return null;
            try
            {
                var candidates = new System.Collections.Generic.List<string>();
                var home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home) && home.StartsWith("/")) candidates.Add(ProcessHost.ToWinePath(home + "/.config/rhinocc/relay.json"));
                // the Linux home is not always visible in the environment: look through the homes on the Z: drive
                if (Directory.Exists(@"Z:\home"))
                    foreach (var dir in Directory.GetDirectories(@"Z:\home")) candidates.Add(Path.Combine(dir, ".config", "rhinocc", "relay.json"));
                foreach (var file in candidates)
                    if (File.Exists(file) && Parse(File.ReadAllText(file)) is RelayConfig c) return c;
            }
            catch (Exception e) { Log.Write("relay config: " + e.Message); }
            return null;
        }

        public bool Reachable()
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var connect = c.ConnectAsync("127.0.0.1", Port);
                    return connect.Wait(1000) && c.Connected;
                }
            }
            catch { return false; }
        }
    }
}
