using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DepotDumper
{
    public sealed record TokenCounts(int Apps, int Packages);

    /// <summary>
    /// Keeps the app and package access tokens Steam hands out during a dump (app.tokens / package.tokens, one "id;token" per line).
    /// Runs add to what is already saved, so the files only grow. They are not secret account data: tokens are the same for every user.
    /// </summary>
    public static class AccessTokens
    {
        public const string AppFile = "app.tokens";
        public const string PackageFile = "package.tokens";

        private static string StoreDir(string dumpDir) => Path.Combine(dumpDir, ".DepotDumper");

        public static TokenCounts Save(string dumpDir, IEnumerable<KeyValuePair<uint, ulong>> apps, IEnumerable<KeyValuePair<uint, ulong>> packages)
        {
            Directory.CreateDirectory(StoreDir(dumpDir));
            return new TokenCounts(Merge(Path.Combine(StoreDir(dumpDir), AppFile), apps), Merge(Path.Combine(StoreDir(dumpDir), PackageFile), packages));
        }

        /// <summary>Copies the saved token files to <paramref name="outDir"/>. Returns how many tokens each holds.</summary>
        public static TokenCounts Export(string dumpDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            int Copy(string name)
            {
                var source = Path.Combine(StoreDir(dumpDir), name);
                if (!File.Exists(source)) return 0;
                File.Copy(source, Path.Combine(outDir, name), overwrite: true);
                return File.ReadLines(source).Count(l => l.Contains(';'));
            }
            return new TokenCounts(Copy(AppFile), Copy(PackageFile));
        }

        public static TokenCounts Count(string dumpDir)
        {
            int Lines(string name)
            {
                var f = Path.Combine(StoreDir(dumpDir), name);
                return File.Exists(f) ? File.ReadLines(f).Count(l => l.Contains(';')) : 0;
            }
            return new TokenCounts(Lines(AppFile), Lines(PackageFile));
        }

        private static int Merge(string file, IEnumerable<KeyValuePair<uint, ulong>> fresh)
        {
            var all = new SortedDictionary<uint, ulong>();
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file))
                {
                    var p = line.Trim().Split(';');
                    if (p.Length == 2 && uint.TryParse(p[0], out var id) && ulong.TryParse(p[1], out var token)) all[id] = token;
                }
            foreach (var kv in fresh)
                if (kv.Value != 0) all[kv.Key] = kv.Value;   // a token of 0 means "none granted": never store that

            var tmp = file + ".tmp";
            File.WriteAllLines(tmp, all.Select(kv => $"{kv.Key};{kv.Value}"));
            File.Move(tmp, file, overwrite: true);
            return all.Count;
        }
    }
}
