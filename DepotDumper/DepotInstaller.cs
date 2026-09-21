// Downloads the files of a depot from a manifest file and its depot key, without needing a Steam license.
// The file/chunk download and in-place update logic is adapted from DepotDownloaderMod (github.com/SteamAutoCracks/DepotDownloaderMod, GPL-2.0),
// itself derived from SteamRE's DepotDownloader, and runs on this app's own Steam session and CDN pool.
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDumper
{
    public static class DepotInstaller
    {
        public sealed record Job(uint AppId, uint DepotId, ulong ManifestId, string ManifestPath, byte[] DepotKey);

        /// <param name="StaleFiles">Full paths of files that belonged to the previous installed version but are not part of this one.</param>
        /// <param name="ReusedBytes">Data taken from files already on disk (unchanged or moved parts of the previous version) instead of downloaded.</param>
        public sealed record Summary(int DepotsDone, int DepotsFailed, ulong Bytes, List<string> Skipped, List<string> StaleFiles, ulong ReusedBytes);

        private sealed record DepotResult(ulong Compressed, ulong Reused, HashSet<string> NewNames, List<string> OldOnlyNames);

        private const int MaxChunkAttempts = 8;
        private const string StateDirName = ".DepotDumper";

        private sealed class FileStreamData
        {
            public FileStream? Stream;
            public readonly SemaphoreSlim Lock = new(1);
            public int ChunksLeft;
        }

        private sealed class Counter
        {
            public ulong Total, Done, Reused;
        }

        private sealed class InstallState
        {
            public Dictionary<uint, ulong> Depots { get; set; } = new();
        }

        // ---- what is installed in a folder (depot -> manifest), so a later run can update it in place instead of starting over

        private static string StatePath(string installDir) => Path.Combine(installDir, StateDirName, "installed.json");

        private static InstallState LoadState(string installDir)
        {
            try { return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(StatePath(installDir))) ?? new InstallState(); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new InstallState(); }
        }

        private static void SaveState(string installDir, InstallState state)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(installDir, StateDirName));
                var tmp = StatePath(installDir) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state));
                File.Move(tmp, StatePath(installDir), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warning($"Could not save the install record: {ex.Message}"); }
        }

        /// <summary>
        /// Installs the selected depots (one folder per app under installRoot). Uses an anonymous Steam connection: chunks come from
        /// Steam's public CDN and are decrypted with the depot key, so the account never needs to own the game.
        /// If the folder already holds another version of a depot, that version is updated in place: unchanged data is reused,
        /// only what differs is downloaded, and files the new version no longer has are reported as stale (never deleted here).
        /// </summary>
        public static async Task<Summary> DownloadAsync(IEnumerable<DowngradeHelper.Item> items, string dumpDir, string installRoot,
            int maxDownloads, Action<string> log, Action<ulong, ulong>? progress, CancellationToken ct)
        {
            var resolved = DowngradeHelper.Resolve(items, dumpDir);
            var skipped = new List<string>();
            skipped.AddRange(resolved.MissingKeys.Select(k => $"{k}: no depot key saved in your dumps"));
            skipped.AddRange(resolved.MissingManifests.Select(m => $"{m}: manifest file not found in your dumps"));
            var jobs = resolved.Ready.Select(r => new Job(r.Item.AppId, r.Item.DepotId, r.Item.ManifestId, r.ManifestPath, r.Key)).ToList();
            if (jobs.Count == 0) return new Summary(0, 0, 0, skipped, new List<string>(), 0);

            log("Connecting to Steam (anonymous)...");
            Steam3Session? steam = null;
            var done = 0; var failed = 0; ulong bytes = 0;
            var stale = new List<string>();
            var counter = new Counter();
            try
            {
                steam = await Task.Run(() =>
                {
                    var s = new Steam3Session(new SteamKit2.SteamUser.LogOnDetails { LoginID = 0x534B34 }, forceAnonymous: true);
                    return s.WaitForCredentials() ? s : null;
                }, ct).ConfigureAwait(false);
                if (steam == null) { log("Could not connect to Steam."); return new Summary(0, jobs.Count, 0, skipped, stale, 0); }
                _ = Task.Run(steam.TickCallbacks, CancellationToken.None);

                counter.Total = jobs.Aggregate(0UL, (sum, j) => sum + TotalSize(j));
                foreach (var byApp in jobs.GroupBy(j => j.AppId))
                {
                    var installDir = Path.Combine(installRoot, byApp.Key.ToString());
                    Directory.CreateDirectory(installDir);
                    var state = LoadState(installDir);
                    var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var oldOnly = new List<string>();
                    var pool = new CDNClientPool(steam, byApp.Key);
                    try
                    {
                        foreach (var job in byApp)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                state.Depots.TryGetValue(job.DepotId, out var installedManifest);
                                var oldManifestPath = installedManifest != 0 && installedManifest != job.ManifestId
                                    ? DowngradeHelper.FindManifestFile(dumpDir, job.AppId, job.DepotId, installedManifest) : null;
                                if (installedManifest != 0 && installedManifest != job.ManifestId)
                                    log(oldManifestPath != null
                                        ? $"Depot {job.DepotId}: updating the installed version ({installedManifest}) to {job.ManifestId}; unchanged data is reused."
                                        : $"Depot {job.DepotId}: the installed version's manifest ({installedManifest}) isn't in your dumps, so existing files are verified instead.");
                                else log($"Depot {job.DepotId} (manifest {job.ManifestId}) -> {installDir}");

                                var r = await InstallDepotAsync(steam, pool, job, oldManifestPath, installDir, Math.Max(1, maxDownloads), counter, log, progress, ct).ConfigureAwait(false);
                                bytes += r.Compressed;
                                newNames.UnionWith(r.NewNames);
                                oldOnly.AddRange(r.OldOnlyNames);
                                state.Depots[job.DepotId] = job.ManifestId;
                                SaveState(installDir, state);
                                done++;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                failed++;
                                log($"Depot {job.DepotId} failed: {ex.Message}");
                                Logger.Error($"Depot install {job.DepotId}/{job.ManifestId} failed: {ex}");
                            }
                        }
                        // files only the previous version had (and no depot of the new version provides)
                        foreach (var name in oldOnly.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !newNames.Contains(n)))
                        {
                            var full = Path.GetFullPath(Path.Combine(installDir, name));
                            if (full.StartsWith(Path.GetFullPath(installDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) stale.Add(full);
                        }
                    }
                    finally { try { pool.Shutdown(); } catch { } }
                }
            }
            finally { try { steam?.Disconnect(); } catch { } }
            return new Summary(done, failed, bytes, skipped, stale, counter.Reused);
        }

        /// <summary>Deletes stale files (after the user agreed). Returns how many were removed.</summary>
        public static int DeleteStale(IEnumerable<string> files)
        {
            var n = 0;
            foreach (var f in files)
            {
                try { File.Delete(f); n++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warning($"Could not delete {f}: {ex.Message}"); }
            }
            return n;
        }

        private static ulong TotalSize(Job job)
        {
            try { return (ulong)DepotManifest.LoadFromFile(job.ManifestPath)!.Files!.Sum(f => (long)f.TotalSize); }
            catch { return 0; }
        }

        private static DepotManifest? LoadOldManifest(string? path, byte[] key, Action<string> log)
        {
            if (path == null) return null;
            try
            {
                var m = DepotManifest.LoadFromFile(path);
                if (m == null) return null;
                if (m.FilenamesEncrypted && !m.DecryptFilenames(key)) return null;
                return m;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { log($"The previous version's manifest could not be read ({ex.Message}); verifying files instead."); return null; }
        }

        private static async Task<DepotResult> InstallDepotAsync(Steam3Session steam, CDNClientPool pool, Job job, string? oldManifestPath, string installDir, int maxDownloads,
            Counter counter, Action<string> log, Action<ulong, ulong>? progress, CancellationToken ct)
        {
            var manifest = DepotManifest.LoadFromFile(job.ManifestPath) ?? throw new InvalidDataException("The manifest file could not be read.");
            if (manifest.FilenamesEncrypted && !manifest.DecryptFilenames(job.DepotKey))
                throw new InvalidDataException("The depot key does not decrypt this manifest's file names (wrong key for this depot?).");
            var oldManifest = LoadOldManifest(oldManifestPath, job.DepotKey, log);
            var oldByName = new Dictionary<string, DepotManifest.FileData>(StringComparer.OrdinalIgnoreCase);
            if (oldManifest?.Files != null)
                foreach (var f in oldManifest.Files.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))) oldByName.TryAdd(f.FileName, f);

            var root = Path.GetFullPath(installDir) + Path.DirectorySeparatorChar;
            string SafePath(string name)
            {
                var full = Path.GetFullPath(Path.Combine(installDir, name));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest file name escapes the install folder: {name}");
                return full;
            }

            var files = manifest.Files!.ToList();
            foreach (var dir in files.Where(f => f.Flags.HasFlag(EDepotFileFlag.Directory))) Directory.CreateDirectory(SafePath(dir.FileName));
            var dataFiles = files.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToList();
            var newNames = new HashSet<string>(dataFiles.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
            var oldOnly = oldByName.Keys.Where(n => !newNames.Contains(n)).ToList();

            var queue = new ConcurrentQueue<(FileStreamData Data, DepotManifest.FileData File, DepotManifest.ChunkData Chunk)>();
            var streams = new ConcurrentBag<FileStreamData>();
            ulong compressed = 0;
            try
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = maxDownloads, CancellationToken = ct };
                await Parallel.ForEachAsync(dataFiles, opts, async (file, _) =>
                {
                    await Task.Yield();
                    var path = SafePath(file.FileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    oldByName.TryGetValue(file.FileName, out var oldFile);
                    var needed = PrepareFile(path, file, oldFile, out var reused);
                    var already = file.TotalSize - (ulong)needed.Sum(c => (long)c.UncompressedLength);
                    if (already > 0) lock (counter) { counter.Done += already; counter.Reused += reused; }
                    if (needed.Count == 0) return;
                    var data = new FileStreamData { ChunksLeft = needed.Count };
                    streams.Add(data);
                    foreach (var chunk in needed) queue.Enqueue((data, file, chunk));
                });

                var reported = DateTime.MinValue;
                await Parallel.ForEachAsync(queue, opts, async (q, _) =>
                {
                    var buffer = ArrayPool<byte>.Shared.Rent((int)q.Chunk.UncompressedLength);
                    try
                    {
                        var written = await DownloadChunkAsync(steam, pool, job, q.Chunk, buffer, ct).ConfigureAwait(false);
                        await q.Data.Lock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            q.Data.Stream ??= File.Open(SafePath(q.File.FileName), FileMode.Open, FileAccess.Write, FileShare.Read);
                            q.Data.Stream.Seek((long)q.Chunk.Offset, SeekOrigin.Begin);
                            await q.Data.Stream.WriteAsync(buffer.AsMemory(0, written), ct).ConfigureAwait(false);
                        }
                        finally { q.Data.Lock.Release(); }
                        if (Interlocked.Decrement(ref q.Data.ChunksLeft) == 0) { q.Data.Stream?.Dispose(); q.Data.Stream = null; }
                        Interlocked.Add(ref compressed, q.Chunk.CompressedLength);
                        lock (counter)
                        {
                            counter.Done += (ulong)written;
                            if (progress != null && DateTime.UtcNow - reported > TimeSpan.FromMilliseconds(500)) { reported = DateTime.UtcNow; progress(counter.Done, counter.Total); }
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                });
            }
            finally
            {
                foreach (var s in streams) { try { s.Stream?.Dispose(); } catch { } }
            }
            progress?.Invoke(counter.Done, counter.Total);
            log($"Depot {job.DepotId} done ({dataFiles.Count} files).");
            return new DepotResult(compressed, counter.Reused, newNames, oldOnly);
        }

        /// <summary>
        /// Creates, resizes or rebuilds the file and returns the chunks that still have to be downloaded.
        /// New file: every chunk. File of the previous version: chunks the two versions share are copied over from the old file
        /// (each one checked against its checksum first). Anything else already on disk: intact chunks are kept, the rest re-fetched.
        /// </summary>
        private static List<DepotManifest.ChunkData> PrepareFile(string path, DepotManifest.FileData file, DepotManifest.FileData? oldFile, out ulong reusedBytes)
        {
            reusedBytes = 0;
            var staging = path + ".ddg-old";
            if (File.Exists(staging))   // an earlier update was interrupted mid-rebuild
            {
                if (!File.Exists(path)) File.Move(staging, path); else File.Delete(staging);
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                using var created = File.Create(path);
                created.SetLength((long)file.TotalSize);
                return [.. file.Chunks];
            }

            var needed = new List<DepotManifest.ChunkData>();
            if (oldFile != null && !oldFile.FileHash.AsSpan().SequenceEqual(file.FileHash) && file.TotalSize > 0)
            {
                // this file changed between the two versions: rebuild it from the old file's chunks that are still valid
                var oldChunks = new Dictionary<string, DepotManifest.ChunkData>();
                foreach (var c in oldFile.Chunks) oldChunks.TryAdd(Convert.ToHexString(c.ChunkID!), c);

                var copies = new List<(DepotManifest.ChunkData Old, DepotManifest.ChunkData New)>();
                foreach (var chunk in file.Chunks)
                {
                    if (oldChunks.TryGetValue(Convert.ToHexString(chunk.ChunkID!), out var old)) copies.Add((old, chunk));
                    else needed.Add(chunk);
                }

                var good = new List<(DepotManifest.ChunkData Old, DepotManifest.ChunkData New)>();
                using (var fsOld = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    foreach (var m in copies.OrderBy(c => c.Old.Offset))
                    {
                        if ((ulong)fsOld.Length >= m.Old.Offset + m.Old.UncompressedLength)
                        {
                            fsOld.Seek((long)m.Old.Offset, SeekOrigin.Begin);
                            if (Adler32(fsOld, (int)m.Old.UncompressedLength) == m.Old.Checksum) { good.Add(m); continue; }
                        }
                        needed.Add(m.New);   // the old data is damaged: fetch it
                    }

                File.Move(path, staging);
                var buffer = ArrayPool<byte>.Shared.Rent(good.Count == 0 ? 1 : good.Max(g => (int)g.Old.UncompressedLength));
                try
                {
                    using (var fsOld = File.Open(staging, FileMode.Open, FileAccess.Read))
                    using (var fs = File.Open(path, FileMode.Create, FileAccess.Write))
                    {
                        fs.SetLength((long)file.TotalSize);
                        foreach (var m in good)
                        {
                            var len = (int)m.Old.UncompressedLength;
                            fsOld.Seek((long)m.Old.Offset, SeekOrigin.Begin);
                            fsOld.ReadExactly(buffer, 0, len);
                            fs.Seek((long)m.New.Offset, SeekOrigin.Begin);
                            fs.Write(buffer, 0, len);
                            reusedBytes += (ulong)len;
                        }
                    }
                    File.Delete(staging);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                return needed;
            }

            // unchanged file, or nothing known about what is there: keep every chunk that checks out
            using var existing = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
            if ((ulong)existing.Length != file.TotalSize) existing.SetLength((long)file.TotalSize);
            foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
            {
                existing.Seek((long)chunk.Offset, SeekOrigin.Begin);
                if (Adler32(existing, (int)chunk.UncompressedLength) != chunk.Checksum) needed.Add(chunk);
                else reusedBytes += chunk.UncompressedLength;
            }
            return needed;
        }

        private static uint Adler32(Stream stream, int length)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var read = 0;
                while (read < length)
                {
                    var n = stream.Read(buffer, read, length - read);
                    if (n <= 0) return uint.MaxValue ^ 1;   // short file: cannot match
                    read += n;
                }
                uint a = 0, b = 0;
                for (var i = 0; i < length; i++) { a = (a + buffer[i]) % 65521; b = (b + a) % 65521; }
                return a | (b << 16);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private static async Task<int> DownloadChunkAsync(Steam3Session steam, CDNClientPool pool, Job job, DepotManifest.ChunkData chunk, byte[] buffer, CancellationToken ct)
        {
            var chunkId = Convert.ToHexString(chunk.ChunkID!).ToLowerInvariant();
            Exception? last = null;
            for (var attempt = 0; attempt < MaxChunkAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Server? connection = null;
                try
                {
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        wait.CancelAfter(TimeSpan.FromSeconds(60));   // never hang forever if Steam hands out no content servers
                        connection = pool.GetConnection(wait.Token);
                    }
                    string? token = null;
                    if (steam.CDNAuthTokens.TryGetValue((job.DepotId, connection.Host), out var promise)) token = (await promise.Task.ConfigureAwait(false)).Token;

                    var written = await pool.CDNClient.DownloadDepotChunkAsync(job.DepotId, chunk, connection, buffer, job.DepotKey, pool.ProxyServer, token).ConfigureAwait(false);
                    pool.ReturnConnection(connection);
                    return written;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (SteamKitWebRequestException e)
                {
                    last = e;
                    if (connection != null && e.StatusCode == HttpStatusCode.Forbidden &&
                        (!steam.CDNAuthTokens.TryGetValue((job.DepotId, connection.Host), out var promise) || !promise.Task.IsCompleted))
                    {
                        await steam.RequestCDNAuthToken(job.AppId, job.DepotId, connection).ConfigureAwait(false);
                        pool.ReturnConnection(connection);
                        continue;   // retry the same chunk with the auth token
                    }
                    pool.ReturnBrokenConnection(connection);
                    if (connection != null && (e.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests))
                        CDNClientPool.MarkBad(connection.Host);
                }
                catch (Exception e)
                {
                    last = e;
                    pool.ReturnBrokenConnection(connection);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + attempt, 5)), ct).ConfigureAwait(false);
            }
            throw new IOException($"chunk {chunkId} could not be downloaded from any server: {last?.Message}");
        }
    }
}
