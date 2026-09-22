using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DepotDumper.Linux;

/// <summary>
/// The Linux/macOS GUI (Avalonia). A small first cut, alongside the same command-line mode Windows has: sign in, start or
/// stop a whole-library dump, watch live progress and the log. The pages the Windows WPF app has beyond that (the
/// manifest/lua libraries, Download version, the game index) aren't ported yet - this window is meant to grow into them.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
