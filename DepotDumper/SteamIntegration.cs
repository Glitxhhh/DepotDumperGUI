using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DepotDumper
{
    public sealed class SendResult
    {
        public int Copied, AlreadyThere, Conflicts, Failed, Total;
        public string Destination;
        public bool SteamRunning;
        public override string ToString() =>
            $"Copied {Copied:N0} manifests to Steam's depotcache ({AlreadyThere:N0} already there" +
            (Conflicts > 0 ? $", {Conflicts:N0} kept because a different file had the same name" : "") +
            (Failed > 0 ? $", {Failed:N0} failed" : "") + ").";
    }

    /// <summary>Talking to the local Steam install: finding it and putting manifests where Steam looks for them.</summary>
    public static class SteamIntegration
    {
        /// <summary>The Steam install folder, from the registry (null if Steam isn't installed for this user).</summary>
        public static string FindSteamRoot()
        {
            try
            {
                using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var p = user?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return Path.GetFullPath(p.Replace('/', '\\'));

                using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                                    ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                p = machine?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return Path.GetFullPath(p);
            }
            catch { /* registry unavailable */ }
            return null;
        }

        public static string DepotCacheDir()
        {
            var root = FindSteamRoot();
            return root == null ? null : Path.Combine(root, "depotcache");
        }

        public static bool IsSteamRunning() => Process.GetProcessesByName("steam").Length > 0;

        /// <summary>Number of pooled manifests that would be considered for copying.</summary>
        public static int CountPooledManifests(string dumpDir)
        {
            var pool = Collector.ManifestDir(dumpDir);
            return Directory.Exists(pool) ? Directory.EnumerateFiles(pool, "*.manifest").Count() : 0;
        }

        /// <summary>
        /// Copies dumps\manifests\*.manifest into Steam's depotcache. Existing files are never overwritten:
        /// same size = already there, different size = left alone and reported.
        /// </summary>
        public static SendResult SendManifestsToDepotCache(string dumpDir, Action<string> log = null, CancellationToken ct = default)
        {
            var dest = DepotCacheDir() ?? throw new InvalidOperationException("Steam's install folder wasn't found in the registry.");
            var pool = Collector.ManifestDir(dumpDir);
            if (!Directory.Exists(pool)) throw new DirectoryNotFoundException("There is no manifests pool yet. Run \"Collect luas & manifests now\" first.");

            Directory.CreateDirectory(dest);
            var res = new SendResult { Destination = dest, SteamRunning = IsSteamRunning() };
            foreach (var src in Directory.EnumerateFiles(pool, "*.manifest"))
            {
                ct.ThrowIfCancellationRequested();
                res.Total++;
                var target = Path.Combine(dest, Path.GetFileName(src));
                try
                {
                    if (File.Exists(target))
                    {
                        if (new FileInfo(target).Length == new FileInfo(src).Length) res.AlreadyThere++;
                        else { res.Conflicts++; log?.Invoke($"Kept existing {Path.GetFileName(target)} (different size)."); }
                        continue;
                    }
                    File.Copy(src, target, overwrite: false);
                    res.Copied++;
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    log?.Invoke($"Could not copy {Path.GetFileName(src)}: {ex.Message}");
                }
            }
            return res;
        }
    }

    /// <summary>Builds the commands used to install an older version of a game from its manifests.</summary>
    public static class DowngradeHelper
    {
        public readonly record struct Item(uint AppId, uint DepotId, ulong ManifestId);

        /// <summary>Type into Steam's console (steam://open/console). Downloads that depot at that manifest.</summary>
        public static string SteamConsole(IEnumerable<Item> items) =>
            string.Join(Environment.NewLine, items.Select(i => $"download_depot {i.AppId} {i.DepotId} {i.ManifestId}"));

        /// <summary>One DepotDownloader command per depot, all into downgrade\&lt;appid&gt;.</summary>
        public static string DepotDownloader(IEnumerable<Item> items, string username)
        {
            var user = string.IsNullOrWhiteSpace(username) ? "YOUR_STEAM_USERNAME" : username.Trim();
            return string.Join(Environment.NewLine, items.Select(i =>
                $"DepotDownloader.exe -app {i.AppId} -depot {i.DepotId} -manifest {i.ManifestId} -username {user} -remember-password -dir \"downgrade\\{i.AppId}\""));
        }

        public static string BatchFile(IEnumerable<Item> items, string username)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("rem Generated by Depot Dumper GUI. Put this next to DepotDownloader.exe and run it.");
            sb.AppendLine("rem Files land in .\\downgrade\\<appid>. Sign in when asked (Steam Guard code if you use one).");
            sb.AppendLine();
            sb.AppendLine(DepotDownloader(items, username));
            sb.AppendLine();
            sb.AppendLine("pause");
            return sb.ToString();
        }
    }
}
