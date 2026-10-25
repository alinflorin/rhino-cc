using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using RhinoCC.Core.Update;

namespace RhinoCC.Chat
{
    /// <summary>Checks GitHub for a newer release, installs it over the plugin's files and tells the panel; the new version runs after Rhino restarts.</summary>
    internal static class UpdateService
    {
        public static string Version { get; } = typeof(UpdateService).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
        public static string PluginDir { get; } = Path.GetDirectoryName(typeof(UpdateService).Assembly.Location);

        /// <summary>A newer release found but not installed (auto-update off, or the install failed).</summary>
        public static ReleaseInfo Available { get; private set; }
        /// <summary>The version already in place, waiting for a restart.</summary>
        public static string Installed { get; private set; }
        /// <summary>What the last manual check came to, for the status line.</summary>
        public static string Message => _message != null && (Busy || DateTime.UtcNow - _at < TimeSpan.FromSeconds(8)) ? _message : null;
        private static string _message;
        private static DateTime _at;
        private static void Say(string m) { _message = m; _at = DateTime.UtcNow; }
        public static bool Busy { get; private set; }

        public static void Start()
        {
            Updater.Cleanup(PluginDir);
#if !DEBUG
            // a Debug build runs from the source tree; a release zip must not land on top of it
            if (AppSettings.AutoUpdate) _ = Task.Delay(8000).ContinueWith(_ => Check(false));
#endif
        }

        public static void Check(bool manual) => _ = Run(manual);

        public static void InstallNow() => _ = Run(true);

        private static async Task Run(bool manual)
        {
            if (Busy || Installed != null) return;
            Busy = true;
            try
            {
                if (manual) Say("Checking for updates…");
                var rel = Available ?? await Updater.CheckAsync(Version, Rhino.RhinoApp.Version.Major).ConfigureAwait(false);
                if (rel == null) { Say(manual ? "RhinoCC is up to date." : null); return; }
                if (!manual && !AppSettings.AutoUpdate) { Available = rel; Say(null); return; }
                Say($"Downloading RhinoCC {rel.Version}…");
                await Updater.InstallAsync(rel, PluginDir).ConfigureAwait(false);
                Available = null;
                Installed = rel.Version;
                Say(null);
            }
            catch (Exception ex)
            {
                RhinoCC.Core.Log.Write("update failed: " + ex);
                Say(manual ? "Update failed: " + ex.Message : null);
            }
            finally { Busy = false; }
        }
    }
}
