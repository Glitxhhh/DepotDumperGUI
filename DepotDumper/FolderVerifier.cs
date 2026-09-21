using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDumper
{
    public sealed record VerifyResult(int Files, int Good, List<string> Mismatched, List<string> Missing, List<string> WrongSize);

    /// <summary>Checks a folder of game files against a depot manifest (file size and SHA-1). Read-only.</summary>
    public static class FolderVerifier
    {
        public static VerifyResult Verify(string manifestPath, byte[] depotKey, string folder, Action<int, int>? progress = null, CancellationToken ct = default)
        {
            var manifest = DepotManifest.LoadFromFile(manifestPath) ?? throw new InvalidDataException("The manifest file could not be read.");
            if (manifest.FilenamesEncrypted && !manifest.DecryptFilenames(depotKey))
                throw new InvalidDataException("The depot key does not decrypt this manifest's file names (wrong key for this depot?).");

            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var files = manifest.Files!.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToList();
            var mismatched = new ConcurrentBag<string>();
            var missing = new ConcurrentBag<string>();
            var wrongSize = new ConcurrentBag<string>();
            var good = 0;
            var done = 0;

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)), CancellationToken = ct }, file =>
            {
                var path = Path.GetFullPath(Path.Combine(folder, file.FileName));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { mismatched.Add(file.FileName + " (name escapes the folder, skipped)"); }
                else if (!File.Exists(path)) missing.Add(file.FileName);
                else if ((ulong)new FileInfo(path).Length != file.TotalSize) wrongSize.Add(file.FileName);
                else
                {
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                        var hash = SHA1.HashData(fs);
                        if (file.TotalSize == 0 || hash.AsSpan().SequenceEqual(file.FileHash)) Interlocked.Increment(ref good);
                        else mismatched.Add(file.FileName);
                    }
                    catch (IOException) { mismatched.Add(file.FileName + " (could not be read)"); }
                    catch (UnauthorizedAccessException) { mismatched.Add(file.FileName + " (access denied)"); }
                }
                progress?.Invoke(Interlocked.Increment(ref done), files.Count);
            });

            return new VerifyResult(files.Count, good, mismatched.OrderBy(x => x).ToList(), missing.OrderBy(x => x).ToList(), wrongSize.OrderBy(x => x).ToList());
        }
    }
}
