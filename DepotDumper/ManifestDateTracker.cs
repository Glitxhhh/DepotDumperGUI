using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace DepotDumper
{
    public class ManifestDateEntry
    {
        public uint DepotId { get; set; }
        public ulong ManifestId { get; set; }
        public string Branch { get; set; }
        public DateTime CreationDate { get; set; }
        public string FolderPath { get; set; }
    }
    public static class ManifestDateTracker
    {
        private static readonly string JsonFilePath;
        private static readonly Dictionary<string, ManifestDateEntry> dateEntries = new Dictionary<string, ManifestDateEntry>();
        private static readonly object fileLock = new object();
        private static bool isDirty = false;
        static ManifestDateTracker()
        {
            string baseDir = DepotDumper.Config?.DumpDirectory ?? DepotDumper.DEFAULT_DUMP_DIR;
            string configDir = Path.Combine(baseDir, DepotDumper.CONFIG_DIR);
            JsonFilePath = Path.Combine(configDir, "manifest_folder_dates.json");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
            LoadFromFile();
        }
        // Generate a unique key for each manifest
        private static string GetKey(uint depotId, ulong manifestId, string branch)
        {
            return $"{depotId}_{manifestId}_{branch.Replace('/', '_')}";
        }
        // Load existing date information from JSON file
        public static void LoadFromFile()
        {
            lock (fileLock)
            {
                try
                {
                    if (File.Exists(JsonFilePath))
                    {
                        string json = File.ReadAllText(JsonFilePath);
                        var entries = JsonSerializer.Deserialize<List<ManifestDateEntry>>(json);
                        if (entries != null)
                        {
                            dateEntries.Clear();
                            foreach (var entry in entries)
                            {
                                string key = GetKey(entry.DepotId, entry.ManifestId, entry.Branch);
                                dateEntries[key] = entry;
                            }
                            Logger.Info($"Loaded {dateEntries.Count} manifest date entries from {JsonFilePath}");
                        }
                    }
                    else
                    {
                        Logger.Info($"Manifest date JSON file not found at {JsonFilePath}, starting with empty data");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error loading manifest dates from JSON: {ex.Message}");
                }
            }
        }
        // Save all current date information to the JSON file
        public static void SaveToFile()
        {
            lock (fileLock)
            {
                if (!isDirty)
                {
                    Logger.Debug("No changes to manifest date tracker, skipping save");
                    return;
                }
                try
                {
                    var entries = dateEntries.Values.ToList();
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    string json = JsonSerializer.Serialize(entries, options);
                    File.WriteAllText(JsonFilePath, json);
                    Logger.Info($"Saved {entries.Count} manifest date entries to {JsonFilePath}");
                    isDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error saving manifest dates to JSON: {ex.Message}");
                }
            }
        }
        // Add or update an entry
        public static void SetEntry(uint depotId, ulong manifestId, string branch, DateTime date, string folderPath = null)
        {
            lock (fileLock)
            {
                string key = GetKey(depotId, manifestId, branch);
                // If entry exists and the folder path is not being updated, preserve the existing path
                if (dateEntries.TryGetValue(key, out var existingEntry) && folderPath == null)
                {
                    folderPath = existingEntry.FolderPath;
                    // If we're not changing anything, don't mark as dirty
                    if (existingEntry.CreationDate == date)
                        return;
                }
                dateEntries[key] = new ManifestDateEntry
                {
                    DepotId = depotId,
                    ManifestId = manifestId,
                    Branch = branch,
                    CreationDate = date,
                    FolderPath = folderPath
                };
                isDirty = true;
            }
        }
        // Get a date entry
        public static ManifestDateEntry GetEntry(uint depotId, ulong manifestId, string branch)
        {
            lock (fileLock)
            {
                string key = GetKey(depotId, manifestId, branch);
                if (dateEntries.TryGetValue(key, out var entry))
                {
                    return entry;
                }
                return null;
            }
        }
        // Get a date directly
        public static DateTime? GetDate(uint depotId, ulong manifestId, string branch)
        {
            var entry = GetEntry(depotId, manifestId, branch);
            return entry?.CreationDate;
        }
        // Update folder path for an entry
        // Update folder paths by scanning directories
        public static void PreloadBranchDatesFromFolders(string baseDirectory, uint appId)
        {
            var folderDates = Util.GetDatesFromFolders(baseDirectory, appId);
            if (folderDates.Count > 0)
            {
                Logger.Info($"Preloaded {folderDates.Count} branch dates from existing folders for app {appId}");
                // Use a public method instead of direct access
                foreach (var kvp in folderDates)
                {
                    DepotDumper.AddUpdateBranchDate(kvp.Key, kvp.Value);
                }
            }
        }
    }
}
