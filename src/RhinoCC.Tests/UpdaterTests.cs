using System.IO;
using System.IO.Compression;
using RhinoCC.Core.Update;
using Xunit;

namespace RhinoCC.Tests
{
    public class UpdaterTests
    {
        [Theory]
        [InlineData("0.1.0", "0.2.0", true)]
        [InlineData("0.1.0", "v0.1.0", false)]
        [InlineData("0.9.0", "0.10.0", true)]
        [InlineData("1.0.0.0", "1.0.1", true)]
        [InlineData("0.2.0", "0.1.9", false)]
        [InlineData("0.1.0", "garbage", false)]
        public void Compares_versions(string cur, string latest, bool newer) => Assert.Equal(newer, Updater.IsNewer(cur, latest));

        const string Json = @"{""tag_name"":""v0.3.0"",""draft"":false,""prerelease"":false,""body"":""notes"",""assets"":[
            {""name"":""RhinoCC-0.3.0-rhino7.zip"",""browser_download_url"":""https://x/7.zip""},
            {""name"":""RhinoCC-0.3.0-rhino8.zip"",""browser_download_url"":""https://x/8.zip""}]}";

        [Fact]
        public void Picks_the_zip_for_the_rhino_version()
        {
            Assert.Equal("https://x/7.zip", Updater.Parse(Json, 7).Url);
            var r = Updater.Parse(Json, 8);
            Assert.Equal("https://x/8.zip", r.Url);
            Assert.Equal("0.3.0", r.Version);
        }

        [Fact]
        public void Ignores_drafts_and_releases_without_a_zip()
        {
            Assert.Null(Updater.Parse(Json.Replace("\"draft\":false", "\"draft\":true"), 8));
            Assert.Null(Updater.Parse(@"{""tag_name"":""v1.0.0"",""assets"":[]}", 8));
        }

        static string Zip(string dir, params (string name, string text)[] files)
        {
            var p = Path.Combine(dir, "u.zip");
            using (var za = ZipFile.Open(p, ZipArchiveMode.Create))
                foreach (var (name, text) in files)
                    using (var w = new StreamWriter(za.CreateEntry(name).Open())) w.Write(text);
            return p;
        }

        [Fact]
        public void Apply_replaces_files_and_cleanup_removes_the_old_ones()
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            var plugin = Directory.CreateDirectory(Path.Combine(dir, "p")).FullName;
            File.WriteAllText(Path.Combine(plugin, "RhinoCC.rhp"), "old");
            var zip = Zip(dir, ("RhinoCC/RhinoCC.rhp", "new"), ("RhinoCC/RhinoCC.Core.dll", "core"), ("RhinoCC/../../evil.txt", "x"));
            Updater.Apply(zip, plugin);
            Assert.Equal("new", File.ReadAllText(Path.Combine(plugin, "RhinoCC.rhp")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(plugin, "RhinoCC.rhp.old")));
            Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
            Updater.Cleanup(plugin);
            Assert.False(File.Exists(Path.Combine(plugin, "RhinoCC.rhp.old")));
        }

        [Fact]
        public void A_zip_without_the_plugin_changes_nothing()
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            File.WriteAllText(Path.Combine(dir, "RhinoCC.rhp"), "old");
            var zip = Zip(dir, ("RhinoCC/other.dll", "x"));
            Assert.Throws<InvalidDataException>(() => Updater.Apply(zip, dir));
            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "RhinoCC.rhp")));
        }
    }
}
