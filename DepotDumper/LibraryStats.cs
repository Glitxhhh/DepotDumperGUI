using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace DepotDumper
{
    public readonly record struct LibraryTotals(int Apps, int Depots, int Manifests);

    /// <summary>
    /// What is already on disk: apps in the dumps folder, depot keys, and unique manifests (loose files, files inside the
    /// zips, the pooled manifests folder and the ledger), so the dashboard numbers start from your existing dump and grow.
    /// </summary>
    public static class LibraryStats
    {
        public static LibraryTotals Compute(string dumpDir, CancellationToken ct = default)
        {
            if (!Directory.Exists(dumpDir)) return default;

            int apps = 0;
            var depots = new HashSet<uint>();
            var manifests = new HashSet<string>(StringComparer.Ordinal);   // "<depot>_<manifest>"

            void AddManifestName(string fileName)
            {
                if (!fileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) return;
                var key = fileName.Substring(0, fileName.Length - ".manifest".Length);
                var us = key.IndexOf('_');
                if (us > 0 && uint.TryParse(key.AsSpan(0, us), out var depot)) { depots.Add(depot); manifests.Add(key); }
            }

            foreach (var appDir in Directory.EnumerateDirectories(dumpDir))
            {
                ct.ThrowIfCancellationRequested();
                var appName = Path.GetFileName(appDir);
                if (!uint.TryParse(appName, out _)) continue;   // luas, manifests, logs, .DepotDumper, ...
                apps++;

                try
                {
                    // depot keys: one "depotid;hexkey" line per depot
                    var keyFile = Path.Combine(appDir, appName + ".key");
                    if (File.Exists(keyFile))
                        foreach (var line in File.ReadLines(keyFile))
                        {
                            var semi = line.IndexOf(';');
                            if (semi > 0 && uint.TryParse(line.AsSpan(0, semi).Trim(), out var d)) depots.Add(d);
                        }

                    foreach (var f in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(f);
                        if (ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase)) AddManifestName(Path.GetFileName(f));
                        else if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                using var zip = ZipFile.OpenRead(f);
                                foreach (var e in zip.Entries) AddManifestName(e.Name);
                            }
                            catch { /* unreadable zip: ignore for counting */ }
                        }
                    }
                }
                catch (IOException) { /* a file vanished mid-scan (a dump is running): fine */ }
                catch (UnauthorizedAccessException) { }
            }

            var pool = Collector.ManifestDir(dumpDir);
            if (Directory.Exists(pool))
                foreach (var f in Directory.EnumerateFiles(pool, "*.manifest")) AddManifestName(Path.GetFileName(f));

            foreach (var e in ManifestLedger.Snapshot().Where(x => x.Downloaded))
            {
                depots.Add(e.DepotId);
                manifests.Add($"{e.DepotId}_{e.ManifestId}");
            }

            return new LibraryTotals(apps, depots.Count, manifests.Count);
        }
    }
}
