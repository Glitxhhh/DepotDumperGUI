using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace DepotDumper
{
    public class ConfigFile
    {
        public string Username { get; set; }

        /// <summary>The Steam password, in memory only. On disk it is stored encrypted as <see cref="PasswordProtected"/>.</summary>
        [JsonIgnore]
        public string Password { get; set; }

        /// <summary>Password encrypted with Windows DPAPI (base64). Only this Windows user on this PC can decrypt it.</summary>
        public string PasswordProtected { get; set; }

        /// <summary>Only used to read config.json files written by older versions, which stored the password as plain text. Never written.</summary>
        [JsonPropertyName("Password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LegacyPlainPassword { get; set; }

        public bool RememberPassword { get; set; } = false;
        public bool UseQrCode { get; set; } = false;
        public int CellID { get; set; } = 0;
        public int MaxDownloads { get; set; } = 4;
        public int MaxServers { get; set; } = 20;
        public uint? LoginID { get; set; } = null;
        public string DumpDirectory { get; set; } = "dumps";
        public bool UseNewNamingFormat { get; set; } = true;
        public int MaxConcurrentApps { get; set; } = 3;
        public string LogLevel { get; set; } = "Info";
        public bool DownloadManifests { get; set; } = true;
        public bool DeleteOldManifests { get; set; } = false;
        public bool DownloadHistoricalManifests { get; set; } = false;
        public bool DynamicConcurrency { get; set; } = true;
        public int MaxParallelDepots { get; set; } = 24;
        public double MaxMemoryGb { get; set; } = 0;
        public bool CollectAfterRun { get; set; } = true;
        public bool CollectPublicOnly { get; set; } = false;
        public bool CollectNoBeta { get; set; } = false;
        public bool CollectLatestOnly { get; set; } = false;
        public bool CollectIncludeSteamDepotCache { get; set; } = false;
        public HashSet<uint> AppIdsToProcess { get; set; } = new HashSet<uint>();
        public HashSet<uint> ExcludedAppIds { get; set; } = new HashSet<uint>();

        [JsonIgnore]
        public Dictionary<uint, bool> AppIDs
        {
            get
            {
                var result = new Dictionary<uint, bool>();
                foreach (var appId in AppIdsToProcess)
                {
                    result[appId] = !ExcludedAppIds.Contains(appId);
                }
                return result;
            }
        }

        private static string DefaultConfigPath => AppPaths.ConfigFile;

        public static ConfigFile Load()
        {
            return Load(DefaultConfigPath);
        }

        public static ConfigFile Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    Logger.Warning("Configuration load path was null or empty, using default in-memory config.");
                    return new ConfigFile();
                }
                if (!File.Exists(path))
                {
                    Logger.Info($"Configuration file not found at '{path}'. Using default settings.");
                    return new ConfigFile();
                }
                Logger.Info($"Loading configuration from '{path}'.");
                string json = File.ReadAllText(path);
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };
                if (string.IsNullOrWhiteSpace(json))
                {
                    Logger.Warning($"Configuration file at '{path}' is empty. Using default settings.");
                    return new ConfigFile();
                }
                var loaded = JsonSerializer.Deserialize<ConfigFile>(json, options) ?? new ConfigFile();
                loaded.DecryptPassword(path);
                return loaded;
            }
            catch (JsonException jsonEx)
            {
                Logger.Error($"Error parsing configuration file '{path}': {jsonEx.Message}. Using default settings.");
                return new ConfigFile();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading configuration file '{path}': {ex.Message}. Using default settings.");
                return new ConfigFile();
            }
        }

        public void Save()
        {
            Save(DefaultConfigPath);
        }

        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                string errorMessage = "Error saving configuration: The provided save path was null or empty.";
                Console.WriteLine(errorMessage);
                Logger.Error(errorMessage);
                if (!string.IsNullOrEmpty(DefaultConfigPath))
                {
                    path = DefaultConfigPath;
                    Logger.Info($"Using default path instead: {DefaultConfigPath}");
                }
                else
                {
                    return;
                }
            }
            try
            {
                string directoryPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
                {
                    Logger.Info($"Creating directory for config file: {directoryPath}");
                    Directory.CreateDirectory(directoryPath);
                }
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                // the password never reaches the file as text: only the DPAPI-encrypted form is written
                PasswordProtected = string.IsNullOrEmpty(Password) ? null : Secrets.Protect(Password);
                LegacyPlainPassword = null;
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(path, json);
                Console.WriteLine($"Configuration saved to {path}");
                Logger.Info($"Configuration saved to {path}");
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error saving configuration file to '{path}': {ex.Message}";
                Console.WriteLine(errorMessage);
                Logger.Error($"{errorMessage} - StackTrace: {ex.StackTrace}");
            }
        }

        /// <summary>Fills <see cref="Password"/> from the encrypted value, and upgrades an old plain-text file to the encrypted form.</summary>
        private void DecryptPassword(string path)
        {
            if (!string.IsNullOrEmpty(PasswordProtected))
            {
                if (Secrets.TryUnprotect(PasswordProtected, out var plain)) Password = plain;
                else Logger.Warning("The saved password in the settings file can't be decrypted (it was saved by another Windows user or on another PC). Enter it again.");
                return;
            }

            if (!string.IsNullOrEmpty(LegacyPlainPassword))
            {
                Password = LegacyPlainPassword;
                LegacyPlainPassword = null;
                try
                {
                    Save(path);   // rewrites the file with the encrypted form and no plain-text password
                    Logger.Info("The saved password was stored as plain text; it is now encrypted for your Windows account.");
                }
                catch (Exception ex) { Logger.Warning($"Could not re-save the settings file with an encrypted password: {ex.Message}"); }
            }
        }

        public string BranchFilter { get; set; } = null;

        public void ApplyToDepotDumperConfig()
        {
            DepotDumper.Config ??= new DumpConfig();
            DepotDumper.Config.RememberPassword = this.RememberPassword;
            DepotDumper.Config.UseQrCode = this.UseQrCode;
            DepotDumper.Config.CellID = this.CellID;
            DepotDumper.Config.MaxDownloads = this.MaxDownloads;
            DepotDumper.Config.MaxServers = this.MaxServers;
            DepotDumper.Config.LoginID = this.LoginID;
            DepotDumper.Config.DumpDirectory = AppPaths.ResolveDumpDir(this.DumpDirectory);   // always absolute from here on
            DepotDumper.Config.UseNewNamingFormat = this.UseNewNamingFormat;
            DepotDumper.Config.LogLevel = this.LogLevel;
            DepotDumper.Config.DownloadManifests = this.DownloadManifests;
            DepotDumper.Config.DeleteOldManifests = this.DeleteOldManifests;
            DepotDumper.Config.DownloadHistoricalManifests = this.DownloadHistoricalManifests;
            DepotDumper.Config.DynamicConcurrency = this.DynamicConcurrency;
            DepotDumper.Config.MaxParallelDepots = this.MaxParallelDepots;
            DepotDumper.Config.MaxMemoryGb = this.MaxMemoryGb;
            DepotDumper.Config.MaxConcurrentApps = this.MaxConcurrentApps;
            DepotDumper.Config.CollectAfterRun = this.CollectAfterRun;
            DepotDumper.Config.CollectPublicOnly = this.CollectPublicOnly;
            DepotDumper.Config.CollectNoBeta = this.CollectNoBeta;
            DepotDumper.Config.CollectLatestOnly = this.CollectLatestOnly;
            DepotDumper.Config.CollectIncludeSteamDepotCache = this.CollectIncludeSteamDepotCache;
            ManifestLedger.UseDirectory(AppPaths.ResolveDumpDir(this.DumpDirectory));
            DepotDumper.Config.BranchFilter = this.BranchFilter;
            
            if (DepotDumper.Config.ExcludedAppIds != null)
            {
                DepotDumper.Config.ExcludedAppIds.Clear();
                foreach (var appId in this.ExcludedAppIds)
                {
                    DepotDumper.Config.ExcludedAppIds.Add(appId);
                }
            }
            
            if (this.ExcludedAppIds.Count > 0)
            {
                Logger.Info($"Applying {this.ExcludedAppIds.Count} app exclusions to DepotDumper.Config");
                Logger.Debug($"Excluded App IDs: {string.Join(", ", this.ExcludedAppIds)}");
            }
        }

        public void MergeCommandLineParameters(string[] args)
        {
            if (Program.HasParameter(args, "-username") || Program.HasParameter(args, "-user"))
            {
                Username = Program.GetParameter<string>(args, "-username") ?? Program.GetParameter<string>(args, "-user");
            }
            if (Program.HasParameter(args, "-password") || Program.HasParameter(args, "-pass"))
            {
                Password = Program.GetParameter<string>(args, "-password") ?? Program.GetParameter<string>(args, "-pass");
            }
            if (Program.HasParameter(args, "-remember-password"))
            {
                RememberPassword = true;
            }
            if (Program.HasParameter(args, "-qr"))
            {
                UseQrCode = true;
            }
            if (Program.HasParameter(args, "-cellid"))
            {
                CellID = Program.GetParameter(args, "-cellid", 0);
            }
            if (Program.HasParameter(args, "-max-downloads"))
            {
                MaxDownloads = Program.GetParameter(args, "-max-downloads", 4);
            }
            if (Program.HasParameter(args, "-max-servers"))
            {
                MaxServers = Program.GetParameter(args, "-max-servers", 20);
            }
            if (Program.HasParameter(args, "-loginid"))
            {
                LoginID = Program.GetParameter<uint?>(args, "-loginid", null);
            }
            if (Program.HasParameter(args, "-dump-directory") || Program.HasParameter(args, "-dir"))
            {
                var cliDir = Program.GetParameter<string>(args, "-dump-directory") ?? Program.GetParameter<string>(args, "-dir");
                if (!string.IsNullOrWhiteSpace(cliDir)) DumpDirectory = Path.GetFullPath(cliDir);   // a path typed on the command line is relative to where it was typed
            }
            if (Program.HasParameter(args, "-max-concurrent-apps"))
            {
                MaxConcurrentApps = Program.GetParameter(args, "-max-concurrent-apps", 1);
            }
            if (Program.HasParameter(args, "-log-level"))
            {
                LogLevel = Program.GetParameter<string>(args, "-log-level", "Info");
            }
            if (Program.HasParameter(args, "-download-manifests"))
            {
                DownloadManifests = Program.GetParameter(args, "-download-manifests", true);
            }
            if (Program.HasParameter(args, "-no-manifests"))
            {
                DownloadManifests = false;
            }
            if (Program.HasParameter(args, "-delete-old-manifests"))
            {
                DeleteOldManifests = true;
            }
            if (Program.HasParameter(args, "-history"))
            {
                DownloadHistoricalManifests = true;
            }
            if (Program.HasParameter(args, "-no-dynamic"))
            {
                DynamicConcurrency = false;     // one depot at a time, like earlier versions
            }
            if (Program.HasParameter(args, "-dynamic"))
            {
                DynamicConcurrency = true;
            }
            if (Program.HasParameter(args, "-max-parallel-depots"))
            {
                MaxParallelDepots = Program.GetParameter(args, "-max-parallel-depots", MaxParallelDepots);
            }
            if (Program.HasParameter(args, "-memory-limit"))
            {
                MaxMemoryGb = Program.GetParameter(args, "-memory-limit", MaxMemoryGb);   // in GB, e.g. 2.5
            }
            if (Program.HasParameter(args, "-no-collect"))
            {
                CollectAfterRun = false;
            }
            if (Program.HasParameter(args, "-collect-public-only"))
            {
                CollectPublicOnly = true;
            }
            if (Program.HasParameter(args, "-collect-no-beta"))
            {
                CollectNoBeta = true;
            }
            if (Program.HasParameter(args, "-collect-latest-only"))
            {
                CollectLatestOnly = true;
            }
            if (Program.HasParameter(args, "-collect-depotcache"))
            {
                CollectIncludeSteamDepotCache = true;
            }
            if (Program.HasParameter(args, "-branch"))
            {
                BranchFilter = Program.GetParameter<string>(args, "-branch");
                Logger.Info($"Branch filter set to: {BranchFilter}");
            }

            int excludeIndex = Program.IndexOfParam(args, "-exclude-app");
            if (excludeIndex > -1 && excludeIndex < args.Length - 1)
            {
                int i = excludeIndex + 1;
                while (i < args.Length && !args[i].StartsWith("-"))
                {
                    if (uint.TryParse(args[i], out uint excludeAppId))
                    {
                        ExcludedAppIds.Add(excludeAppId);
                        Logger.Info($"Added app {excludeAppId} to exclusion list from command line");
                        
                        if (!AppIdsToProcess.Contains(excludeAppId))
                        {
                            AppIdsToProcess.Add(excludeAppId);
                            Logger.Debug($"Added excluded app {excludeAppId} to main app list for tracking");
                        }
                    }
                    i++;
                }
            }
            
            int includeIndex = Program.IndexOfParam(args, "-include-app");
            if (includeIndex > -1 && includeIndex < args.Length - 1)
            {
                int i = includeIndex + 1;
                while (i < args.Length && !args[i].StartsWith("-"))
                {
                    if (uint.TryParse(args[i], out uint includeAppId))
                    {
                        if (!AppIdsToProcess.Contains(includeAppId))
                        {
                            AppIdsToProcess.Add(includeAppId);
                            Logger.Info($"Added app {includeAppId} to processing list from command line");
                        }
                        
                        if (ExcludedAppIds.Contains(includeAppId))
                        {
                            ExcludedAppIds.Remove(includeAppId);
                            Logger.Info($"Removed app {includeAppId} from exclusion list as explicitly included from command line");
                        }
                    }
                    i++;
                }
            }
            
            ApplyToDepotDumperConfig();
        }
    }
}