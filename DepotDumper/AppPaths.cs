using System;
using System.IO;

namespace DepotDumper
{
    /// <summary>
    /// Where the app keeps its files.
    ///   Installed mode (default for new users)
    ///     settings, login and GUI prefs : %LOCALAPPDATA%\DepotDumperGUI
    ///     dumps                          : Documents\DepotDumperGUI\dumps   (visible, can be large, can be moved anywhere)
    ///   Portable mode - everything next to the exe. Used when a "portable.txt" marker sits beside the exe, or when a
    ///   config.json already sits beside the exe and none exists in AppData (so existing setups keep working untouched).
    /// The mode can be switched at runtime from Settings; files are copied across, never deleted.
    /// </summary>
    public static class AppPaths
    {
        private const string MarkerName = "portable.txt";
        private const string ConfigName = "config.json";
        private const string AccountName = "account.config";
        private const string GuiSettingsName = "gui.settings.json";

        static AppPaths()
        {
            IsPortable = File.Exists(Path.Combine(ExeDir, MarkerName))
                         || (File.Exists(Path.Combine(ExeDir, ConfigName)) && !File.Exists(Path.Combine(LocalDataDir, ConfigName)));
        }

        public static string ExeDir => AppContext.BaseDirectory;
        public static string LocalDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepotDumperGUI");
        public static string DocumentsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DepotDumperGUI");

        public static bool IsPortable { get; private set; }

        public static string SettingsDir => IsPortable ? ExeDir : LocalDataDir;
        public static string ConfigFile => Path.Combine(SettingsDir, ConfigName);
        public static string AccountFile => Path.Combine(SettingsDir, AccountName);
        public static string GuiSettingsFile => Path.Combine(SettingsDir, GuiSettingsName);

        /// <summary>Folder that a relative dump directory (like the default "dumps") is relative to.</summary>
        public static string DumpsRoot => IsPortable ? ExeDir : DocumentsDir;

        /// <summary>Turns the configured dump directory into an absolute path. Absolute paths are used as they are.</summary>
        public static string ResolveDumpDir(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) configured = DepotDumper.DEFAULT_DUMP_DIR;
            configured = configured.Trim();
            return Path.IsPathRooted(configured) ? Path.GetFullPath(configured) : Path.GetFullPath(Path.Combine(DumpsRoot, configured));
        }

        public static void EnsureSettingsDir()
        {
            try { Directory.CreateDirectory(SettingsDir); } catch { /* reported when a save fails */ }
        }

        /// <summary>
        /// Switches between installed and portable mode: copies the settings files to the new home, then flips the marker.
        /// Nothing is deleted. Takes effect immediately for later saves.
        /// </summary>
        public static void SwitchMode(bool portable)
        {
            if (portable == IsPortable) return;

            var from = SettingsDir;
            var to = portable ? ExeDir : LocalDataDir;
            Directory.CreateDirectory(to);
            foreach (var name in new[] { ConfigName, AccountName, GuiSettingsName })
            {
                var src = Path.Combine(from, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(to, name), overwrite: true);
            }

            var marker = Path.Combine(ExeDir, MarkerName);
            if (portable) File.WriteAllText(marker, "Depot Dumper GUI portable mode: settings and dumps live next to the exe.\r\nDelete this file (or turn portable mode off in Settings) to store settings in AppData instead.\r\n");
            else if (File.Exists(marker)) File.Delete(marker);

            IsPortable = portable;
        }
    }
}
