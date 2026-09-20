using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DepotDumper
{
    public sealed class CollectOptions
    {
        public string DumpDir { get; set; }
        /// <summary>Only take files from the "public" branch.</summary>
        public bool PublicOnly { get; set; }
        /// <summary>Skip beta-like branches (beta, preview, experimental, prerelease, test, ...).</summary>
        public bool NoBeta { get; set; }
        /// <summary>Keep exactly one lua per app: the newest by dump date.</summary>
        public bool LatestOnly { get; set; }
        /// <summary>Also pool manifests found in Steam's own depotcache folder.</summary>
        public bool IncludeSteamDepotCache { get; set; }
        /// <summary>Pool manifests only (used to seed the manifest history without touching luas).</summary>
        public bool ManifestsOnly { get; set; }

        public const string LuaFolderName = "luas";
        public const string ManifestFolderName = "manifests";
        public const string DefaultBetaPattern = @"(?i)beta|preview|alpha|experimental|unstable|nightly|canary|pre-?release|release[_-]?candidate|(^|[^a-z])(test(ing)?|dev(elopment)?|internal|staging|rc\d*)([^a-z]|$)";
    }

    public sealed class CollectResult
    {
        public int Scanned, Zips, ZipErrors, SkippedByBranch, OlderLuasDropped;
        public int LuasNew, LuasReplaced, LuasDuplicate, ManifestsNew, ManifestsDuplicate;
        public List<string> LuaConflicts { get; } = new List<string>();
        public List<string> ManifestConflicts { get; } = new List<string>();
        public string LuaDir, ManifestDir;

        public override string ToString()
        {
            var repl = LuasReplaced > 0 ? $", {LuasReplaced} replaced with newer" : "";
            return $"Luas: {LuasNew} new{repl}, {LuasDuplicate} duplicates skipped  ->  {LuaDir}\n" +
                   $"Manifests: {ManifestsNew} new, {ManifestsDuplicate} duplicates skipped  ->  {ManifestDir}" +
                   (SkippedByBranch > 0 ? $"\nSkipped by branch filter: {SkippedByBranch}" : "") +
                   (OlderLuasDropped > 0 ? $"\nOlder lua versions dropped: {OlderLuasDropped}" : "") +
                   (LuaConflicts.Count > 0 ? $"\nLuas kept side by side (same name, different content): {LuaConflicts.Count}" : "") +
                   (ZipErrors > 0 ? $"\nUnreadable zips: {ZipErrors}" : "");
        }
    }

    /// <summary>
    /// Pools every .lua and .manifest under the dump directory (loose files and files inside the .zip archives) into
    /// dumps\luas and dumps\manifests, de-duplicating by SHA-256. Same name with different content is kept side by
    /// side (hash-suffixed) unless LatestOnly is set for luas.
    /// </summary>
    public static class Collector
    {
        private static readonly Regex ZipFolderRx = new Regex(@"^(?<app>\d+)\.(?<branch>.+?)\.(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.", RegexOptions.Compiled);
        private static readonly Regex ManifestNameRx = new Regex(@"^(?<depot>\d+)_(?<gid>\d+)\.manifest$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private sealed class Candidate
        {
            public bool IsLua;
            public string Name;
            public DateTime Date;
            public string Zip;      // null for loose files
            public string Entry;
            public string Path;
            public string Origin;
            public uint AppId;
            public string Branch;
        }

        private sealed class Pool
        {
            public string Dir;
            public Dictionary<string, string> Hashes = new Dictionary<string, string>();   // sha -> full path
            public HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Conflicts = new List<string>();
            public int New, Replaced, Duplicate;
        }

        public static string LuaDir(string dumpDir) => Path.Combine(dumpDir, CollectOptions.LuaFolderName);
        public static string ManifestDir(string dumpDir) => Path.Combine(dumpDir, CollectOptions.ManifestFolderName);

        public static string FindSteamDepotCache()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var steamPath = key?.GetValue("SteamPath") as string;
                if (string.IsNullOrWhiteSpace(steamPath)) return null;
                var dir = Path.Combine(steamPath.Replace('/', '\\'), "depotcache");
                return Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }

        private static Pool CreatePool(string dir)
        {
            Directory.CreateDirectory(dir);
            var p = new Pool { Dir = dir };
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = System.IO.Path.GetFileName(f);
                if (name.Contains(".corrupt-")) continue;
                p.Hashes[ManifestLedger.ComputeSha256(File.ReadAllBytes(f))] = f;
                p.Names.Add(name);
            }
            return p;
        }

        public static bool BranchAllowed(CollectOptions o, string branch, bool isLua)
        {
            // Manifests pulled from history / Steam's depotcache aren't tied to a branch, so filters don't apply to them.
            if (!isLua && (branch == "depotcache" || branch == "history")) return true;
            if (o.PublicOnly && !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase)) return false;
            if (o.NoBeta && !string.IsNullOrEmpty(branch) && Regex.IsMatch(branch, CollectOptions.DefaultBetaPattern)) return false;
            return true;
        }

        public static CollectResult Run(CollectOptions o, Action<string> log = null, Action<int, int> progress = null, CancellationToken ct = default)
        {
            log ??= _ => { };
            var dumpDir = Path.GetFullPath(string.IsNullOrWhiteSpace(o.DumpDir) ? DepotDumper.DEFAULT_DUMP_DIR : o.DumpDir);
            if (!Directory.Exists(dumpDir)) throw new DirectoryNotFoundException($"Dump directory not found: {dumpDir}");

            var res = new CollectResult { LuaDir = LuaDir(dumpDir), ManifestDir = ManifestDir(dumpDir) };
            var lua = CreatePool(res.LuaDir);
            var man = CreatePool(res.ManifestDir);

            // ---- pass 1: enumerate candidates, applying branch filters ----
            var cands = new List<Candidate>();
            foreach (var appDir in Directory.EnumerateDirectories(dumpDir))
            {
                ct.ThrowIfCancellationRequested();
                var appName = Path.GetFileName(appDir);
                if (!uint.TryParse(appName, out var appId)) continue;   // skips luas, manifests, logs, .DepotDumper, ...

                foreach (var f in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lua" && ext != ".manifest" && ext != ".zip") continue;
                    res.Scanned++;

                    var (branch, date) = GetMeta(dumpDir, f);
                    if (ext == ".zip")
                    {
                        res.Zips++;
                        if (!BranchAllowedForZip(o, branch)) { res.SkippedByBranch++; continue; }
                        try
                        {
                            using var zip = ZipFile.OpenRead(f);
                            foreach (var e in zip.Entries)
                            {
                                if (string.IsNullOrEmpty(e.Name)) continue;
                                var eext = Path.GetExtension(e.Name).ToLowerInvariant();
                                if (eext != ".lua" && eext != ".manifest") continue;
                                if (o.ManifestsOnly && eext == ".lua") continue;
                                cands.Add(new Candidate { IsLua = eext == ".lua", Name = e.Name, Date = date, Zip = f, Entry = e.FullName, Path = f, Origin = $"{f}!{e.FullName}", AppId = appId, Branch = branch });
                            }
                        }
                        catch (Exception ex)
                        {
                            res.ZipErrors++;
                            log($"Could not read zip '{f}': {ex.Message}");
                        }
                    }
                    else
                    {
                        bool isLua = ext == ".lua";
                        if (o.ManifestsOnly && isLua) continue;
                        if (!BranchAllowed(o, branch, isLua)) { res.SkippedByBranch++; continue; }
                        cands.Add(new Candidate { IsLua = isLua, Name = Path.GetFileName(f), Date = date, Path = f, Origin = f, AppId = appId, Branch = branch });
                    }
                }
            }

            if (o.IncludeSteamDepotCache)
            {
                var cache = FindSteamDepotCache();
                if (cache != null)
                {
                    foreach (var f in Directory.EnumerateFiles(cache, "*.manifest"))
                        cands.Add(new Candidate { IsLua = false, Name = Path.GetFileName(f), Date = File.GetLastWriteTime(f), Path = f, Origin = f, Branch = "depotcache" });
                    log($"Included Steam depotcache: {cache}");
                }
                else log("Steam depotcache folder not found; skipping.");
            }

            // ---- LatestOnly: one lua per name, newest dump date wins ----
            if (o.LatestOnly)
            {
                var luaC = cands.Where(c => c.IsLua).ToList();
                var keep = luaC.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                               .Select(g => g.OrderByDescending(c => c.Date).ThenBy(c => c.Origin, StringComparer.Ordinal).First())
                               .ToList();
                res.OlderLuasDropped = luaC.Count - keep.Count;
                cands = cands.Where(c => !c.IsLua).Concat(keep).ToList();
            }

            // ---- pass 2: read, hash, write ----
            int i = 0;
            foreach (var c in cands)
            {
                ct.ThrowIfCancellationRequested();
                if (++i % 100 == 0) progress?.Invoke(i, cands.Count);

                byte[] bytes;
                try { bytes = ReadCandidate(c); }
                catch (Exception ex) { log($"Skipped unreadable '{c.Origin}': {ex.Message}"); continue; }

                if (c.IsLua)
                {
                    AddItem(lua, c.Name, bytes, c.Origin, replace: o.LatestOnly);
                }
                else
                {
                    bool isNew = AddItem(man, c.Name, bytes, c.Origin, replace: false);
                    var m = ManifestNameRx.Match(c.Name);
                    if (m.Success && uint.TryParse(m.Groups["depot"].Value, out var depot) && ulong.TryParse(m.Groups["gid"].Value, out var gid))
                    {
                        // Everything pooled becomes part of the known-manifest history.
                        ManifestLedger.Record(depot, gid, c.AppId, c.Branch, ManifestLedger.ComputeSha256(bytes), bytes.Length, "collected");
                    }
                }
            }
            progress?.Invoke(cands.Count, cands.Count);
            ManifestLedger.SaveToFile();

            res.LuasNew = lua.New; res.LuasReplaced = lua.Replaced; res.LuasDuplicate = lua.Duplicate;
            res.ManifestsNew = man.New; res.ManifestsDuplicate = man.Duplicate;
            res.LuaConflicts.AddRange(lua.Conflicts);
            res.ManifestConflicts.AddRange(man.Conflicts);
            return res;
        }

        // Zips are named <app>.<branch>.<date>.<name>; their branch filter is decided by the folder name.
        private static bool BranchAllowedForZip(CollectOptions o, string branch) => BranchAllowed(o, branch, isLua: true) || BranchAllowed(o, branch, isLua: false);

        private static (string branch, DateTime date) GetMeta(string dumpDir, string file)
        {
            var rel = Path.GetRelativePath(dumpDir, file);
            var parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string branch = null;
            DateTime date = File.GetLastWriteTime(file);
            if (parts.Length >= 3)
            {
                var m = ZipFolderRx.Match(parts[1]);
                if (m.Success)
                {
                    branch = m.Groups["branch"].Value;
                    if (DateTime.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd_HH-mm-ss", null, System.Globalization.DateTimeStyles.None, out var d)) date = d;
                }
                else branch = parts[1];
            }
            return (branch, date);
        }

        private static byte[] ReadCandidate(Candidate c)
        {
            if (c.Zip != null)
            {
                using var z = ZipFile.OpenRead(c.Zip);
                using var s = z.GetEntry(c.Entry).Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            return File.ReadAllBytes(c.Path);
        }

        /// <returns>true if a new file was written (or an existing one replaced)</returns>
        private static bool AddItem(Pool p, string name, byte[] bytes, string origin, bool replace)
        {
            var h = ManifestLedger.ComputeSha256(bytes);
            var dest = Path.Combine(p.Dir, name);

            if (replace)
            {
                // newest-wins: <name> must end up holding exactly this content
                if (p.Hashes.TryGetValue(h, out var existing) && string.Equals(existing, dest, StringComparison.OrdinalIgnoreCase)) { p.Duplicate++; return false; }
                bool existed = p.Names.Contains(name);
                if (existed)
                    foreach (var k in p.Hashes.Where(kv => string.Equals(kv.Value, dest, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                        p.Hashes.Remove(k);
                File.WriteAllBytes(dest, bytes);
                p.Hashes[h] = dest; p.Names.Add(name);
                if (existed) p.Replaced++; else p.New++;
                return true;
            }

            if (p.Hashes.ContainsKey(h)) { p.Duplicate++; return false; }

            var target = name;
            if (p.Names.Contains(name))
            {
                var ext = Path.GetExtension(name);
                target = $"{Path.GetFileNameWithoutExtension(name)}.{h.Substring(0, 8)}{ext}";
                p.Conflicts.Add($"{name}  ->  {target}   (from {origin})");
                dest = Path.Combine(p.Dir, target);
            }
            File.WriteAllBytes(dest, bytes);
            p.Hashes[h] = dest; p.Names.Add(target);
            p.New++;
            return true;
        }
    }
}
