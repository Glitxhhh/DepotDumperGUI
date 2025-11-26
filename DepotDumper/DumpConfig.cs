using System.Collections.Generic;

namespace DepotDumper
{
    class DumpConfig
    {
        public int CellID { get; set; }
        public string DumpDirectory { get; set; }
        public int MaxServers { get; set; }
        public int MaxDownloads { get; set; } = 4;
        public bool RememberPassword { get; set; }
        public uint? LoginID { get; set; }
        public bool UseQrCode { get; set; }
        public bool UseNewNamingFormat { get; set; } = true;
        public string LogLevel { get; set; } = "Info";
        public bool DownloadManifests { get; set; } = true;
        public HashSet<uint> ExcludedAppIds { get; set; } = new HashSet<uint>();
        public string BranchFilter { get; set; } = null; // Filter to specific branch (e.g., "public")
    }
}