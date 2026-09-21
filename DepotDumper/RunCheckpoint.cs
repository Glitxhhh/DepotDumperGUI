using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DepotDumper
{
    /// <summary>
    /// Remembers which apps a whole-library dump has finished, so an interrupted run (Stop, Force shutdown, crash, closing the app,
    /// updating to a new build) can be resumed instead of walking every app again. Stored next to the ledger in the dumps folder as
    /// plain JSON. Unknown or older/newer files are simply ignored, never fatal.
    /// </summary>
    public static class RunCheckpoint
    {
        private const int CurrentSchema = 1;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

        public sealed class Data
        {
            public int Schema { get; set; } = CurrentSchema;
            public string Account { get; set; } = "";
            public string Scope { get; set; } = "";
            public DateTime StartedUtc { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public bool Completed { get; set; }
            public int Planned { get; set; }
            public string Version { get; set; } = "";
            public List<uint> Done { get; set; } = new();
        }

        public sealed record Info(int Done, int Planned, DateTime StartedUtc, DateTime UpdatedUtc);

        private static readonly object gate = new();
        private static string? path;
        private static Data? data;
        private static HashSet<uint> done = new();
        private static DateTime lastWrite = DateTime.MinValue;

        /// <summary>Anything that changes what "finished" means for an app must be part of the scope, or a resumed run would skip work it shouldn't.</summary>
        public static string ScopeKey(string? branchFilter, bool downloadManifests) =>
            $"library|branch={(string.IsNullOrWhiteSpace(branchFilter) ? "*" : branchFilter.Trim().ToLowerInvariant())}|manifests={(downloadManifests ? 1 : 0)}";

        private static string FileFor(string dumpDir) => Path.Combine(dumpDir, ".DepotDumper", "run_checkpoint.json");

        private static Data? Read(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(file));
                return d is { Schema: <= CurrentSchema } ? d : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>Returns details of an interrupted run that can be resumed with the same account and settings, or null.</summary>
        public static Info? FindResumable(string dumpDir, string account, string scope)
        {
            var d = Read(FileFor(dumpDir));
            if (d == null || d.Completed || d.Done.Count == 0) return null;
            if (!string.Equals(d.Account, account ?? "", StringComparison.OrdinalIgnoreCase) || d.Scope != scope) return null;
            if (DateTime.UtcNow - d.UpdatedUtc > MaxAge) return null;
            return new Info(d.Done.Count, d.Planned, d.StartedUtc, d.UpdatedUtc);
        }

        public static void Begin(string dumpDir, string account, string scope, int planned, bool resume, string version)
        {
            lock (gate)
            {
                path = FileFor(dumpDir);
                var existing = resume ? Read(path) : null;
                var usable = existing != null && !existing.Completed && string.Equals(existing.Account, account ?? "", StringComparison.OrdinalIgnoreCase)
                             && existing.Scope == scope && DateTime.UtcNow - existing.UpdatedUtc <= MaxAge;
                data = usable ? existing! : new Data { Account = account ?? "", Scope = scope, StartedUtc = DateTime.UtcNow };
                data.Planned = planned;
                data.Version = version;
                data.Completed = false;
                done = new HashSet<uint>(data.Done);
                Write();
                if (usable) Logger.Info($"Resuming an interrupted run: {done.Count} apps already finished are skipped.");
            }
        }

        /// <summary>Writes the current progress right now (used when the app is closed while a run is going).</summary>
        public static void Flush() { lock (gate) Write(); }

        public static bool IsDone(uint appId) { lock (gate) return data != null && done.Contains(appId); }

        public static void MarkDone(uint appId)
        {
            lock (gate)
            {
                if (data == null || !done.Add(appId)) return;
                if (DateTime.UtcNow - lastWrite > TimeSpan.FromSeconds(3)) Write();   // a crash loses at most the last few seconds of progress
            }
        }

        /// <summary>Finishes the run. completed = every app was visited, so there is nothing left to resume.</summary>
        public static void End(bool completed)
        {
            lock (gate)
            {
                if (data == null) return;
                data.Completed = completed;
                Write();
                data = null;
                path = null;
                done = new HashSet<uint>();
            }
        }

        private static void Write()
        {
            if (data == null || path == null) return;
            try
            {
                data.Done = done.OrderBy(x => x).ToList();
                data.UpdatedUtc = DateTime.UtcNow;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                File.Move(tmp, path, overwrite: true);
                lastWrite = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warning($"Could not save the resume checkpoint: {ex.Message}"); }
        }
    }
}
