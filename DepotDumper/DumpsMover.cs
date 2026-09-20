using System;
using System.IO;
using System.Linq;

namespace DepotDumper
{
    /// <summary>Moves the whole dumps folder (apps, luas, manifests, logs, ledger) to a new location.</summary>
    public static class DumpsMover
    {
        public static string Move(string source, string target, Action<string> log = null)
        {
            source = Path.GetFullPath(source).TrimEnd('\\', '/');
            target = Path.GetFullPath(target).TrimEnd('\\', '/');

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The new folder is the same as the current one.");
            if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The new folder can't be inside the current dumps folder.");
            if (source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The new folder can't contain the current dumps folder.");
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Nothing to move: {source} doesn't exist.");
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new InvalidOperationException("The new folder isn't empty. Choose an empty folder so nothing gets mixed up.");

            Directory.CreateDirectory(target);
            bool sameDrive = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
            int files = 0; long bytes = 0;

            if (sameDrive)
            {
                // fast path: entries are renamed, not copied
                foreach (var entry in Directory.EnumerateFileSystemEntries(source).ToList())
                {
                    var dest = Path.Combine(target, Path.GetFileName(entry));
                    if (Directory.Exists(entry)) { CountDir(entry, ref files, ref bytes); Directory.Move(entry, dest); }
                    else { files++; bytes += new FileInfo(entry).Length; File.Move(entry, dest); }
                }
            }
            else
            {
                // different drive: copy everything, verify sizes, and only then remove the original
                CopyDir(source, target, log, ref files, ref bytes);
                log?.Invoke($"Copied {files:N0} files; verifying...");
                VerifyDir(source, target);
                Directory.Delete(source, recursive: true);
            }

            try { if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source); } catch { }
            return $"Moved {files:N0} files ({bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB) to {target}.";
        }

        private static void CountDir(string dir, ref int files, ref long bytes)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { files++; bytes += new FileInfo(f).Length; }
        }

        private static void CopyDir(string src, string dst, Action<string> log, ref int files, ref long bytes)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src))
            {
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false);
                files++; bytes += new FileInfo(f).Length;
                if (files % 500 == 0) log?.Invoke($"Copied {files:N0} files...");
            }
            foreach (var d in Directory.EnumerateDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), log, ref files, ref bytes);
        }

        private static void VerifyDir(string src, string dst)
        {
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(dst, Path.GetRelativePath(src, f));
                if (!File.Exists(copy) || new FileInfo(copy).Length != new FileInfo(f).Length)
                    throw new IOException($"Verification failed for {Path.GetRelativePath(src, f)}; the original folder was left untouched.");
            }
        }
    }
}
