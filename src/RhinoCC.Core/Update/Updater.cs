using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RhinoCC.Core.Update
{
    public sealed class ReleaseInfo
    {
        public string Version;
        public string Url;
        public string Notes;
    }

    /// <summary>Finds a newer release on GitHub and swaps the plugin's files for it. The new version runs after Rhino restarts.</summary>
    public static class Updater
    {
        public const string LatestUrl = "https://api.github.com/repos/alinflorin/rhino-cc/releases/latest";

        /// <summary>True when <paramref name="latest"/> is a higher release than <paramref name="current"/>; "v" and prerelease suffixes are ignored.</summary>
        public static bool IsNewer(string current, string latest)
        {
            return TryParse(current, out var c) && TryParse(latest, out var l) && l > c;
        }

        public static bool TryParse(string text, out Version v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim().TrimStart('v', 'V');
            var cut = text.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) text = text.Substring(0, cut);
            var parts = text.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            if (parts.Length < 3) text += ".0";
            return Version.TryParse(text, out v);
        }

        /// <summary>The release described by GitHub's "latest" answer, with the zip for this Rhino; null when it has none.</summary>
        public static ReleaseInfo Parse(string json, int rhinoMajor)
        {
            var o = JObject.Parse(json);
            var tag = (string)o["tag_name"];
            if (string.IsNullOrEmpty(tag) || (bool?)o["draft"] == true || (bool?)o["prerelease"] == true) return null;
            var suffix = rhinoMajor >= 8 ? "-rhino8.zip" : "-rhino7.zip";
            var asset = (o["assets"] as JArray)?.FirstOrDefault(a => ((string)a["name"] ?? "").EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            var url = (string)asset?["browser_download_url"];
            if (url == null) return null;
            return new ReleaseInfo { Version = tag.TrimStart('v', 'V'), Url = url, Notes = (string)o["body"] ?? "" };
        }

        private static HttpClient Client()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // TLS 1.2 on .NET Framework
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RhinoCC-updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        /// <summary>The newer release for this Rhino, or null when this one is current.</summary>
        public static async Task<ReleaseInfo> CheckAsync(string current, int rhinoMajor)
        {
            using (var http = Client())
            {
                var json = await http.GetStringAsync(LatestUrl).ConfigureAwait(false);
                var r = Parse(json, rhinoMajor);
                return r != null && IsNewer(current, r.Version) ? r : null;
            }
        }

        /// <summary>Downloads the release and replaces the plugin's files in <paramref name="pluginDir"/>.</summary>
        public static async Task InstallAsync(ReleaseInfo release, string pluginDir)
        {
            var zip = Path.Combine(Path.GetTempPath(), "rhinocc-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var http = Client())
                {
                    var bytes = await http.GetByteArrayAsync(release.Url).ConfigureAwait(false);
                    File.WriteAllBytes(zip, bytes);
                }
                Apply(zip, pluginDir);
            }
            finally { try { File.Delete(zip); } catch { } }
        }

        /// <summary>
        /// Puts the zip's files (the release zip has one folder, RhinoCC/) into the plugin folder. Files Rhino has loaded cannot be overwritten
        /// but can be renamed, so each one is moved aside as *.old first; those are removed by <see cref="Cleanup"/> at the next start.
        /// Any failure puts the old files back.
        /// </summary>
        public static void Apply(string zipPath, string pluginDir)
        {
            var moved = new System.Collections.Generic.List<string>();
            var written = new System.Collections.Generic.List<string>();
            try
            {
                using (var za = ZipFile.OpenRead(zipPath))
                {
                    if (!za.Entries.Any(e => e.Name.Equals("RhinoCC.rhp", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("the download has no RhinoCC.rhp");
                    foreach (var e in za.Entries)
                    {
                        if (e.Name.Length == 0 || e.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;
                        var dest = Path.Combine(pluginDir, e.Name); // the file name only: nothing in the zip can write outside the folder
                        if (File.Exists(dest))
                        {
                            var old = dest + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(dest, old);
                            moved.Add(dest);
                        }
                        written.Add(dest);
                        e.ExtractToFile(dest, true);
                    }
                }
            }
            catch
            {
                foreach (var f in written) try { File.Delete(f); } catch { }
                foreach (var f in moved) try { File.Move(f + ".old", f); } catch { }
                throw;
            }
        }

        /// <summary>Removes the files an earlier update moved aside.</summary>
        public static void Cleanup(string pluginDir)
        {
            try { foreach (var f in Directory.GetFiles(pluginDir, "*.old")) try { File.Delete(f); } catch { } }
            catch { }
        }
    }
}
