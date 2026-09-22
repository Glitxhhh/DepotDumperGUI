using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using DepotDumper.Parsers;

namespace DepotDumper
{
    /// <summary>One pooled lua file (dumps\luas\*.lua) with what could be read from it.</summary>
    public sealed class LuaEntry
    {
        public uint AppId { get; init; }
        public string App => AppId == 0 ? "—" : AppId.ToString();
        /// <summary>The app this lua belongs to: every variant of an app sits under one group.</summary>
        public string Group => AppId == 0 ? "Unknown app" : string.IsNullOrEmpty(Name) ? App : $"{App}  ·  {Name}";
        public string Name { get; init; } = "";
        public string Variant { get; init; } = "";
        public int Keys { get; init; }
        public int Others { get; init; }          // addappid lines without a key (DLC and similar)
        public int ManifestCount { get; init; }
        public int ManifestsDownloaded { get; init; }
        public string Manifests => ManifestCount == 0 ? "—" : $"{ManifestsDownloaded} / {ManifestCount}";
        public string Modified { get; init; } = "";
        /// <summary>Creation date of the newest manifest this lua points at: orders variants newest -> oldest.</summary>
        public DateTime? Newest { get; init; }
        public string NewestText => Newest?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        public string Issues { get; init; } = "";
        public string Path { get; init; } = "";
        public string FileName => System.IO.Path.GetFileName(Path);
        public string SearchText { get; init; } = "";
    }

    /// <summary>Reads the pooled luas so they can be browsed, searched and checked. Read-only: nothing here changes a file.</summary>
    public static class LuaLibrary
    {
        public static string AppNameFor(string dumpDir, uint appId) => ReadAppName(dumpDir, appId);

        /// <summary>Name of a DLC from dumps\&lt;base&gt;\&lt;dlc&gt;.dlcinfo ("dlcId;name;DLC_For_base"), or "".</summary>
        public static string DlcNameFor(string dumpDir, uint baseAppId, uint dlcAppId)
        {
            try
            {
                var f = System.IO.Path.Combine(dumpDir, baseAppId.ToString(), dlcAppId + ".dlcinfo");
                if (!File.Exists(f)) return "";
                var parts = (File.ReadLines(f).FirstOrDefault() ?? "").Split(';');
                return parts.Length >= 2 ? parts[1].Trim() : "";
            }
            catch (IOException) { return ""; }
        }

        public static List<LuaEntry> Load(string dumpDir, CancellationToken ct = default)
        {
            var result = new List<LuaEntry>();
            var luaDir = Collector.LuaDir(dumpDir);
            if (!Directory.Exists(luaDir)) return result;
            var manifestDir = Collector.ManifestDir(dumpDir);
            ManifestLedger.UseDirectory(dumpDir);

            var keyFiles = new Dictionary<uint, Dictionary<uint, string>>();   // app -> depot -> key from the app's own .key file
            var names = new Dictionary<uint, string>();

            foreach (var file in Directory.EnumerateFiles(luaDir, "*.lua"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stem = System.IO.Path.GetFileNameWithoutExtension(file);
                    var parts = stem.Split('.', 2);
                    if (!uint.TryParse(parts[0], out var appId)) continue;
                    var variant = parts.Length > 1 ? parts[1] : "";

                    var parsed = LuaParser.Parse(File.ReadAllText(file));
                    var keyed = parsed.KeyedDepots;
                    var others = parsed.BareAppIds.Count(id => uint.TryParse(id, out var parsedId) && parsedId != appId);
                    var manifests = parsed.Pins
                        .Where(p => uint.TryParse(p.Key, out _) && ulong.TryParse(p.Value, out _))
                        .Select(p => (Depot: uint.Parse(p.Key), Manifest: ulong.Parse(p.Value)))
                        .ToList();
                    var downloaded = Directory.Exists(manifestDir)
                        ? manifests.Count(m => File.Exists(System.IO.Path.Combine(manifestDir, $"{m.Depot}_{m.Manifest}.manifest")))
                        : 0;

                    DateTime? newest = null;
                    foreach (var (depotId, manifestId) in manifests)
                        if (ManifestLedger.TryGet(depotId, manifestId, out var known) && known.CreatedUtc is { } made && (newest == null || made > newest)) newest = made;

                    if (!names.TryGetValue(appId, out var name)) names[appId] = name = ReadAppName(dumpDir, appId);
                    if (!keyFiles.TryGetValue(appId, out var knownKeys)) keyFiles[appId] = knownKeys = ReadKeyFile(dumpDir, appId);

                    var issues = new List<string>();
                    if (keyed.Count == 0) issues.Add("no depot keys");
                    var differing = keyed.Count(kv => uint.TryParse(kv.Key, out var parsedDepot) && knownKeys.TryGetValue(parsedDepot, out var k) && !string.Equals(k, kv.Value, StringComparison.OrdinalIgnoreCase));
                    if (differing > 0) issues.Add($"{differing} key(s) differ from the .key file");
                    if (manifests.Count > downloaded) issues.Add($"{manifests.Count - downloaded} manifest(s) not downloaded");

                    result.Add(new LuaEntry
                    {
                        AppId = appId, Name = name, Variant = variant, Keys = keyed.Count, Others = others,
                        ManifestCount = manifests.Count, ManifestsDownloaded = downloaded,
                        Modified = File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm"), Newest = newest,
                        Issues = string.Join("; ", issues), Path = file,
                        SearchText = $"{appId} {name} {variant} {string.Join(' ', issues)}",
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip a file that can't be read */ }
            }
            return result.OrderBy(e => e.AppId).ThenByDescending(e => e.Newest ?? DateTime.MinValue).ThenBy(e => e.Variant, StringComparer.Ordinal).ToList();
        }

        private static string ReadAppName(string dumpDir, uint appId)
        {
            try
            {
                var info = System.IO.Path.Combine(dumpDir, appId.ToString(), appId + ".info");
                if (!File.Exists(info)) return "";
                var line = File.ReadLines(info).FirstOrDefault() ?? "";
                var i = line.IndexOf(';');
                return i >= 0 ? line[(i + 1)..].Trim() : "";
            }
            catch (IOException) { return ""; }
        }

        private static Dictionary<uint, string> ReadKeyFile(string dumpDir, uint appId)
        {
            var map = new Dictionary<uint, string>();
            try
            {
                var file = System.IO.Path.Combine(dumpDir, appId.ToString(), appId + ".key");
                if (!File.Exists(file)) return map;
                foreach (var line in File.ReadLines(file))
                {
                    var p = line.Trim().Split(';');
                    if (p.Length >= 2 && uint.TryParse(p[0], out var depot)) map.TryAdd(depot, p[1]);
                }
            }
            catch (IOException) { }
            return map;
        }
    }
}
