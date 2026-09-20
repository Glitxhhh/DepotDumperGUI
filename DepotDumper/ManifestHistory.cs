using System;
using System.IO;
using System.Text.RegularExpressions;

namespace DepotDumper
{
    public readonly record struct ImportResult(int Added, int AlreadyKnown, int Invalid);

    /// <summary>Getting manifest IDs into the <see cref="ManifestLedger"/> from outside the dumper.</summary>
    public static class ManifestHistory
    {
        private static readonly Regex Digits = new Regex(@"\d+", RegexOptions.Compiled);

        /// <summary>
        /// Imports manifest IDs from a text/CSV file. One entry per line, any separators, '#' comments allowed:
        ///   depotid manifestid              (2 numbers)
        ///   appid depotid manifestid        (3 numbers)
        ///   123456_9876543210987654321.manifest   (file names work too)
        /// Imported IDs are "pending": a run with historical downloads enabled fetches them for depots you own.
        /// </summary>
        public static ImportResult ImportIds(string file, Action<string> log = null)
        {
            int added = 0, known = 0, invalid = 0;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw;
                int hash = line.IndexOf('#'); if (hash >= 0) line = line.Substring(0, hash);
                int slashes = line.IndexOf("//", StringComparison.Ordinal); if (slashes >= 0) line = line.Substring(0, slashes);
                var nums = Digits.Matches(line);
                if (nums.Count == 0) continue;   // blank line or header

                uint appId = 0, depotId; ulong manifestId;
                bool ok;
                if (nums.Count == 2)
                    ok = uint.TryParse(nums[0].Value, out depotId) & ulong.TryParse(nums[1].Value, out manifestId);
                else if (nums.Count == 3)
                {
                    ok = uint.TryParse(nums[0].Value, out appId) & uint.TryParse(nums[1].Value, out depotId) & ulong.TryParse(nums[2].Value, out manifestId);
                }
                else { invalid++; continue; }

                if (!ok || depotId == 0 || manifestId == 0) { invalid++; continue; }
                if (ManifestLedger.Record(depotId, manifestId, appId, null, null, 0, "imported")) added++; else known++;
            }
            ManifestLedger.SaveToFile();
            log?.Invoke($"Imported {added} new manifest IDs ({known} already known, {invalid} lines skipped).");
            return new ImportResult(added, known, invalid);
        }
    }
}
