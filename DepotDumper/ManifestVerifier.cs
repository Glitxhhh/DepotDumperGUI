using System;
using System.IO;
using SteamKit2;

namespace DepotDumper
{
    public enum ManifestCheckResult
    {
        Valid,
        Corrupt,
    }

    /// <summary>
    /// Validates a manifest already on disk without needing ".sha" sidecar files.
    ///  - structure: must parse, and its depot / manifest IDs must match the file name
    ///  - consistency: the file entries' sizes must add up to the size the manifest declares
    ///  - integrity: SHA-256 must match the hash recorded in the <see cref="ManifestLedger"/> when it was first written/verified
    /// A manifest not in the ledger yet (e.g. from a previous version) is trusted once it passes the structural
    /// checks, and its hash is recorded so later corruption is detectable.
    /// </summary>
    public static class ManifestVerifier
    {
        public static ManifestCheckResult Verify(string path, uint depotId, ulong manifestId, uint appId, string branch, out string reason)
        {
            reason = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    reason = "file is missing or empty";
                    return ManifestCheckResult.Corrupt;
                }

                string sha = ManifestLedger.ComputeSha256(path);

                if (ManifestLedger.TryGet(depotId, manifestId, out var known) && !string.IsNullOrEmpty(known.Sha256))
                {
                    if (!string.Equals(known.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = $"SHA-256 {sha[..12]}... does not match ledger {known.Sha256[..12]}... (recorded {known.FirstSeenUtc:u})";
                        return ManifestCheckResult.Corrupt;
                    }
                    // Hash matches what we stored: no need to re-parse a (possibly huge) manifest.
                    ManifestLedger.Record(depotId, manifestId, appId, branch, null, 0, "verified");
                    return ManifestCheckResult.Valid;
                }

                // Not in the ledger yet: full structural validation.
                var manifest = DepotManifest.LoadFromFile(path);
                if (manifest == null)
                {
                    reason = "manifest could not be parsed";
                    return ManifestCheckResult.Corrupt;
                }
                if (manifest.DepotID != depotId || manifest.ManifestGID != manifestId)
                {
                    reason = $"IDs inside the file (depot {manifest.DepotID}, manifest {manifest.ManifestGID}) do not match its name";
                    return ManifestCheckResult.Corrupt;
                }

                ulong sum = 0;
                foreach (var f in manifest.Files) sum += f.TotalSize;
                if (sum != manifest.TotalUncompressedSize)
                {
                    reason = $"file sizes add up to {sum} but the manifest declares {manifest.TotalUncompressedSize}";
                    return ManifestCheckResult.Corrupt;
                }

                ManifestLedger.Record(depotId, manifestId, appId, branch, sha, info.Length, "verified");
                return ManifestCheckResult.Valid;
            }
            catch (Exception ex)
            {
                reason = $"{ex.GetType().Name}: {ex.Message}";
                return ManifestCheckResult.Corrupt;
            }
        }

        /// <summary>Moves a bad file aside (never deletes it) so the manifest can be downloaded again.</summary>
        public static void Quarantine(string path)
        {
            try
            {
                var dest = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(path, dest);
                Logger.Warning($"Moved corrupt manifest aside: {Path.GetFileName(dest)}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not move corrupt manifest {path} aside: {ex.Message}");
            }
        }
    }
}
