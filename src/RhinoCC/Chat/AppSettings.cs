using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RhinoCC.Core.Agents;

namespace RhinoCC.Chat
{
    /// <summary>The plugin's persistent settings (Rhino keeps them per user, outside the model).</summary>
    internal static class AppSettings
    {
        public const int DefaultPort = 45600;

        // The first default (a stub); settings saved with it unchanged are moved to the current default.
        private const string StubInstructions =
            "You are working inside Rhino 3D with the user, who is looking at the viewports while you work: every change you make appears live in front of them.\n" +
            "- Run `status` first. Work in small, deliberate steps and say what you are doing.\n" +
            "- You cannot see the screen, but you can see the model with `shot`. Never claim the model looks right without having looked at a picture.\n" +
            "- Prefer the commands of the `rhino` line over `cmd`/`script`; use those for what the line cannot do.\n" +
            "- Ask before deleting things you did not create.";

        public static string DefaultInstructions { get; } = LoadDefaultInstructions();

        private static string LoadDefaultInstructions()
        {
            using (var s = typeof(AppSettings).Assembly.GetManifestResourceStream("DefaultInstructions.md"))
            {
                if (s == null) return StubInstructions;
                using (var r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8)) return r.ReadToEnd().Replace("\r\n", "\n").Trim();
            }
        }

        private static Rhino.PersistentSettings S => RhinoCCPlugin.Instance.Settings;

        public static string DefaultAgent { get => S.GetString("agent", "claude"); set => S.SetString("agent", value); }
        // "" = the agent's own default
        public static string DefaultModel { get => S.GetString("model", ""); set => S.SetString("model", value ?? ""); }
        public static string DefaultEffort { get => S.GetString("effort", ""); set => S.SetString("effort", value ?? ""); }
        public static string ClaudePath { get => S.GetString("claudePath", "claude"); set => S.SetString("claudePath", value); }
        public static string CodexPath { get => S.GetString("codexPath", "codex"); set => S.SetString("codexPath", value); }
        public static string HostShell { get => S.GetString("hostShell", "/bin/sh"); set => S.SetString("hostShell", value); }
        public static int Port { get => S.GetInteger("port", DefaultPort); set => S.SetInteger("port", value); }
        public static bool OpenAtStartup { get => S.GetBool("openAtStartup", true); set => S.SetBool("openAtStartup", value); }
        public static bool AutoUpdate { get => S.GetBool("autoUpdate", true); set => S.SetBool("autoUpdate", value); }
        public static bool AllTools { get => S.GetBool("allTools", false); set => S.SetBool("allTools", value); }
        public static bool ChimeOn { get => S.GetBool("chimeOn", true); set => S.SetBool("chimeOn", value); }
        /// <summary>A reply that took at least this many seconds ends with a chime.</summary>
        public static int ChimeSecs { get => S.GetInteger("chimeSecs", 20); set => S.SetInteger("chimeSecs", Math.Max(0, value)); }

        /// <summary>The plan limits an agent last reported; they only change when it answers, so they are kept between runs.</summary>
        public static List<LimitInfo> LimitsOf(string agent)
        {
            try { return JsonConvert.DeserializeObject<List<LimitInfo>>(S.GetString("limits." + agent, "[]")) ?? new List<LimitInfo>(); }
            catch { return new List<LimitInfo>(); }
        }

        public static void SetLimits(string agent, List<LimitInfo> limits) => S.SetString("limits." + agent, JsonConvert.SerializeObject(limits));

        public static string Instructions
        {
            get
            {
                var stored = S.GetString("instructions", DefaultInstructions);
                return stored.Trim() == StubInstructions ? DefaultInstructions : stored;
            }
            set => S.SetString("instructions", value);
        }

        public static AgentSettings ForAgents() => new AgentSettings { ClaudePath = ClaudePath, CodexPath = CodexPath, HostShell = HostShell };
    }
}
