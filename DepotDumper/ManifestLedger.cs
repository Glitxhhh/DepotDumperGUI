using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DepotDumper
{
    /// <summary>One manifest the dumper knows about. An entry without a hash is a known ID that hasn't been downloaded yet.</summary>
    public class ManifestLedgerEntry
    {
        public uint DepotId { get; set; }
        public ulong ManifestId { get; set; }
        public uint AppId { get; set; }
        public List<string> Branches { get; set; } = new List<string>();
        /// <summary>SHA-256 (lowercase hex) of the .manifest file. Null if the ID is known but not downloaded.</summary>
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        /// <summary>How it got here: "downloaded", "verified", "seeded", "imported", "history", ...</summary>
        public string Source { get; set; }
        /// <summary>Set when Steam would not serve this manifest to the account (removed, not owned, ...).</summary>
        public bool Unavailable { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public string Note { get; set; }
        /// <summary>Branches for which Steam gave no manifest request code (the same manifest ID can work on another branch).</summary>
        public List<string> UnavailableBranches { get; set; } = new List<string>();

        public bool Downloaded => !string.IsNullOrEmpty(Sha256);
    }

    public readonly record struct LedgerStats(int Total, int Downloaded, int Pending, int Unavailable);

    /// <summary>
    /// Persistent record of every manifest seen or known. Replaces the old per-file ".sha" sidecars
    /// (one JSON file, nothing extra next to the manifests) and doubles as the manifest-ID history.
    /// </summary>
    public static class ManifestLedger
    {
        private const int SaveEveryNChanges = 200;
        private static readonly TimeSpan RetryUnavailableAfter = TimeSpan.FromDays(3);

        private static string jsonFilePath;
        private static readonly Dictionary<string, ManifestLedgerEntry> entries = new Dictionary<string, ManifestLedgerEntry>();
        private static readonly object fileLock = new object();
        private static int unsavedChanges = 0;

        private static string GetKey(uint depotId, ulong manifestId) => $"{depotId}_{manifestId}";

        /// <summary>Points the ledger at a dump directory (saving any pending changes for the previous one first).</summary>
        public static void UseDirectory(string dumpDir)
        {
            if (string.IsNullOrWhiteSpace(dumpDir)) dumpDir = DepotDumper.DEFAULT_DUMP_DIR;
            var path = Path.Combine(dumpDir, DepotDumper.CONFIG_DIR, "manifest_ledger.json");
            lock (fileLock)
            {
                if (string.Equals(jsonFilePath, path, StringComparison.OrdinalIgnoreCase)) return;
                if (jsonFilePath != null) SaveLocked();
                jsonFilePath = path;
                entries.Clear();
                unsavedChanges = 0;
                LoadLocked();
            }
        }

        private static void EnsureInit()
        {
            if (jsonFilePath == null) UseDirectory(DepotDumper.Config?.DumpDirectory);
        }

        public static bool TryGet(uint depotId, ulong manifestId, out ManifestLedgerEntry entry)
        {
            EnsureInit();
            lock (fileLock) { return entries.TryGetValue(GetKey(depotId, manifestId), out entry); }
        }

        public static LedgerStats GetStats()
        {
            EnsureInit();
            lock (fileLock)
            {
                int downloaded = entries.Values.Count(e => e.Downloaded);
                int unavailable = entries.Values.Count(e => !e.Downloaded && e.Unavailable);
                return new LedgerStats(entries.Count, downloaded, entries.Count - downloaded - unavailable, unavailable);
            }
        }

        public static List<ManifestLedgerEntry> Snapshot()
        {
            EnsureInit();
            lock (fileLock) { return entries.Values.ToList(); }
        }

        /// <summary>Known IDs for a depot that still need downloading (unavailable ones are retried after a few days).</summary>
        public static List<ManifestLedgerEntry> GetPendingForDepot(uint depotId)
        {
            EnsureInit();
            var cutoff = DateTime.UtcNow - RetryUnavailableAfter;
            lock (fileLock)
            {
                return entries.Values
                    .Where(e => e.DepotId == depotId && !e.Downloaded &&
                                (!e.Unavailable || (e.LastAttemptUtc ?? DateTime.MinValue) < cutoff))
                    .OrderByDescending(e => e.ManifestId)
                    .ToList();
            }
        }

        /// <summary>Records (or refreshes) a manifest. Passing a hash marks it downloaded; existing first-seen date is kept.</summary>
        /// <returns>true if this manifest ID was not known before.</returns>
        public static bool Record(uint depotId, ulong manifestId, uint appId, string branch, string sha256, long size, string source)
        {
            EnsureInit();
            lock (fileLock)
            {
                var key = GetKey(depotId, manifestId);
                var now = DateTime.UtcNow;
                bool isNew = false;
                if (!entries.TryGetValue(key, out var e))
                {
                    e = new ManifestLedgerEntry { DepotId = depotId, ManifestId = manifestId, FirstSeenUtc = now };
                    entries[key] = e;
                    isNew = true;
                }
                if (appId != 0) e.AppId = appId;
                if (!string.IsNullOrEmpty(branch) && !e.Branches.Contains(branch)) e.Branches.Add(branch);
                if (!string.IsNullOrEmpty(sha256))
                {
                    e.Sha256 = sha256; e.Size = size;
                    e.Unavailable = false; e.Note = null;
                    if (!string.IsNullOrEmpty(branch)) e.UnavailableBranches.Remove(branch);   // it works on THIS branch now; other branches keep their own refusal
                }
                e.LastSeenUtc = now;
                e.Source ??= source;

                if (++unsavedChanges >= SaveEveryNChanges) SaveLocked();
                return isNew;
            }
        }

        public static void MarkUnavailable(uint depotId, ulong manifestId, string note, string branch = null)
        {
            EnsureInit();
            lock (fileLock)
            {
                if (!entries.TryGetValue(GetKey(depotId, manifestId), out var e)) return;
                e.Unavailable = true;
                e.LastAttemptUtc = DateTime.UtcNow;
                e.Note = note;
                if (!string.IsNullOrEmpty(branch) && !e.UnavailableBranches.Contains(branch)) e.UnavailableBranches.Add(branch);
                if (++unsavedChanges >= SaveEveryNChanges) SaveLocked();
            }
        }

        /// <summary>
        /// True if Steam recently (within the retry window) refused to give a request code for this manifest on this branch,
        /// so a regular run can skip it instead of asking again. Only trusts entries that name this exact branch.
        /// </summary>
        public static bool IsRecentlyUnavailable(uint depotId, ulong manifestId, string branch)
        {
            EnsureInit();
            lock (fileLock)
            {
                return entries.TryGetValue(GetKey(depotId, manifestId), out var e)
                       && !string.IsNullOrEmpty(branch) && e.UnavailableBranches.Contains(branch)   // per-branch: it may be downloaded fine on another branch
                       && (e.LastAttemptUtc ?? DateTime.MinValue) >= DateTime.UtcNow - RetryUnavailableAfter;
            }
        }

        /// <summary>Clears the "unavailable" flag so the next run tries those manifests again.</summary>
        public static int ResetUnavailable()
        {
            EnsureInit();
            lock (fileLock)
            {
                int n = 0;
                foreach (var e in entries.Values.Where(x => x.Unavailable && !x.Downloaded))
                {
                    e.Unavailable = false; e.LastAttemptUtc = null; e.Note = null; e.UnavailableBranches.Clear(); n++;
                }
                if (n > 0) { unsavedChanges += n; SaveLocked(); }
                return n;
            }
        }

        public static void SaveToFile()
        {
            EnsureInit();
            lock (fileLock) { SaveLocked(); }
        }

        private static void SaveLocked()
        {
            if (unsavedChanges == 0 || jsonFilePath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(jsonFilePath));
                var list = entries.Values.OrderBy(x => x.DepotId).ThenBy(x => x.ManifestId).ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                var tmp = jsonFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, jsonFilePath, overwrite: true);
                unsavedChanges = 0;
                Logger.Info($"Saved {list.Count} manifest ledger entries to {jsonFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving manifest ledger: {ex.Message}");
            }
        }

        private static void LoadLocked()
        {
            try
            {
                if (!File.Exists(jsonFilePath)) return;
                var list = JsonSerializer.Deserialize<List<ManifestLedgerEntry>>(File.ReadAllText(jsonFilePath));
                if (list == null) return;
                foreach (var e in list) entries[GetKey(e.DepotId, e.ManifestId)] = e;
                Logger.Info($"Loaded {entries.Count} manifest ledger entries from {jsonFilePath}");
            }
            catch (Exception ex)
            {
                // Don't silently start over on a damaged ledger; keep it for inspection.
                Logger.Error($"Error loading manifest ledger (starting empty, old file kept as .bad): {ex.Message}");
                try { File.Move(jsonFilePath, jsonFilePath + ".bad", overwrite: true); } catch { }
            }
        }

        public static string ComputeSha256(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        public static string ComputeSha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}
