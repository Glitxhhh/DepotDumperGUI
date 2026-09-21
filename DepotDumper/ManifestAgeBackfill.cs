using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDumper
{
    /// <summary>
    /// Fills in the creation date of manifests the ledger already knows (from before dates were recorded) by reading the
    /// manifest files once. Runs quietly in the background; new manifests get their date when they are downloaded.
    /// </summary>
    public static class ManifestAgeBackfill
    {
        private static int running;

        public static async Task<int> RunAsync(string dumpDir, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return 0;
            try
            {
                ManifestLedger.UseDirectory(dumpDir);
                var todo = ManifestLedger.Snapshot().Where(e => e.Downloaded && e.CreatedUtc == null).ToList();
                if (todo.Count == 0) return 0;
                var done = 0;
                await Task.Run(() => Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, e =>
                {
                    try
                    {
                        var path = DowngradeHelper.FindManifestFile(dumpDir, e.AppId, e.DepotId, e.ManifestId);
                        if (path == null) return;
                        var manifest = DepotManifest.LoadFromFile(path);
                        if (manifest == null || manifest.CreationTime.Year < 2000) return;
                        ManifestLedger.SetCreated(e.DepotId, e.ManifestId, manifest.CreationTime.ToUniversalTime());
                        Interlocked.Increment(ref done);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.IO.InvalidDataException) { /* unreadable file: leave it undated */ }
                }), ct).ConfigureAwait(false);
                ManifestLedger.SaveToFile();
                return done;
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }
    }
}
