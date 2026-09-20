using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DepotDumper.GUI;

/// <summary>Settings that belong to the GUI itself (not the dumper): saved next to the exe as gui.settings.json.</summary>
public class GuiSettings
{
    public string Theme { get; set; } = "Dark";
    /// <summary>Write the Steam password into config.json. Off = it is only kept in memory for the session.</summary>
    public bool SavePassword { get; set; } = true;

    public static GuiSettings Current { get; private set; } = new GuiSettings();
    private static string FilePath => AppPaths.GuiSettingsFile;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath)) ?? new GuiSettings();
        }
        catch { Current = new GuiSettings(); }
    }

    public static void Save()
    {
        try { AppPaths.EnsureSettingsDir(); File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* not critical */ }
    }
}

public partial class App : Application
{
    private const string ControlsUri = "pack://application:,,,/DepotDumper;component/Themes/Controls.xaml";

    public App()
    {
        GuiSettings.Load();
        Resources.MergedDictionaries.Add(new ResourceDictionary());   // slot 0: palette (swapped on theme change)
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ControlsUri) });
        ApplyTheme(GuiSettings.Current.Theme);
    }

    public static bool IsDark => !string.Equals(GuiSettings.Current.Theme, "Light", StringComparison.OrdinalIgnoreCase);

    public static void ApplyTheme(string theme)
    {
        var name = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        GuiSettings.Current.Theme = name;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/DepotDumper;component/Themes/{name}.xaml") };
        Current.Resources.MergedDictionaries[0] = palette;
    }
}
