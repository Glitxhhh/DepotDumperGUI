using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DepotDumper
{
    public sealed record KeyExportResult(int Keys, int Apps, int Conflicts);

    /// <summary>Merges every app's .key file in the dumps folder into one "depotId;hexKey" list (the format most depot tools read).</summary>
    public static class KeyExport
    {
        public static KeyExportResult Run(string dumpDir, string outputFile)
        {
            var keys = new SortedDictionary<uint, string>();
            var apps = 0;
            var conflicts = 0;
            foreach (var appDir in Directory.EnumerateDirectories(dumpDir))
            {
                var name = Path.GetFileName(appDir);
                if (!uint.TryParse(name, out _)) continue;
                var found = false;
                foreach (var file in Directory.EnumerateFiles(appDir, "*.key"))
                    foreach (var line in File.ReadLines(file))
                    {
                        var p = line.Trim().Split(';');
                        if (p.Length < 2 || !uint.TryParse(p[0], out var depot) || p[1].Length == 0) continue;
                        found = true;
                        if (keys.TryGetValue(depot, out var existing))
                        {
                            if (!string.Equals(existing, p[1], StringComparison.OrdinalIgnoreCase)) conflicts++;   // keep the first one seen
                        }
                        else keys[depot] = p[1];
                    }
                if (found) apps++;
            }

            var tmp = outputFile + ".tmp";
            File.WriteAllLines(tmp, keys.Select(k => $"{k.Key};{k.Value}"));
            File.Move(tmp, outputFile, overwrite: true);
            return new KeyExportResult(keys.Count, apps, conflicts);
        }
    }
}
