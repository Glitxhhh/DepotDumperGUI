using System.Collections.Generic;

namespace DepotDumper
{
    class DumpConfig
    {
        public int CellID { get; set; }
        public string DumpDirectory { get; set; }
        public int MaxServers { get; set; }
        public int MaxDownloads { get; set; } = 4;
        public int MaxConcurrentApps { get; set; } = 3;
        public bool RememberPassword { get; set; }
        public uint? LoginID { get; set; }
        public bool UseQrCode { get; set; }
        public bool UseNewNamingFormat { get; set; } = true;
        public string LogLevel { get; set; } = "Info";
        public bool DownloadManifests { get; set; } = true;
        public bool DeleteOldManifests { get; set; } = false; // keep older manifests by default (history for downgrading)
        public bool DynamicConcurrency { get; set; } = true;   // auto-adjust how many depots run at once
        public int MaxParallelDepots { get; set; } = 24;       // ceiling for dynamic speed (it only reaches it while Steam and the PC keep up)
        public double MaxMemoryGb { get; set; } = 0;           // optional cap on this app's own memory; 0 = automatic
        public bool DownloadHistoricalManifests { get; set; } = false; // fetch known past manifest IDs into dumps\manifests

        // Pool luas/manifests into dumps\luas and dumps\manifests
        public bool CollectAfterRun { get; set; } = true;
        public bool CollectPublicOnly { get; set; } = false;
        public bool CollectNoBeta { get; set; } = false;
        public bool CollectLatestOnly { get; set; } = false;
        public bool CollectIncludeSteamDepotCache { get; set; } = false;
        public HashSet<uint> ExcludedAppIds { get; set; } = new HashSet<uint>();
        public string BranchFilter { get; set; } = null; // Filter to specific branch (e.g., "public")
    }
}