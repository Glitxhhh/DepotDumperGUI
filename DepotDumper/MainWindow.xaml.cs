using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DepotDumper.GUI;

public class LogLine
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = "";
    public int Severity { get; init; }
}

public class LedgerRow
{
    public string Status { get; init; } = "";
    public string App { get; init; } = "";
    public uint AppId { get; init; }
    public bool IsDownloaded { get; init; }
    public uint Depot { get; init; }
    public ulong Manifest { get; init; }
    public string Branches { get; init; } = "";
    public string FirstSeen { get; init; } = "";
    public string Source { get; init; } = "";
    public string SearchText { get; init; } = "";
}

/// <summary>Depot Dumper GUI main window.</summary>
public partial class MainWindow : Window
{
    private ConfigFile currentConfig;
    private bool isRunning;
    private bool toolBusy;
    private bool loading = true;       // suppresses setting-changed handlers while the UI is being filled from config
    private DateTime runStart;
    private int tick;

    private readonly ConcurrentQueue<LogLine> logQueue = new();
    private readonly ObservableCollection<LogLine> logLines = new();
    private readonly ObservableCollection<LogLine> activity = new();
    private readonly ICollectionView logView;
    private List<LedgerRow> ledgerRows = new();
    private readonly DispatcherTimer uiTimer;

    public MainWindow()
    {
        try { Environment.CurrentDirectory = AppContext.BaseDirectory; } catch { /* keep the launch directory */ }

        Logger.Initialize(null, LogLevel.Info, toConsole: false, toFile: false);   // no stray log file until the dump folder is known
        InitializeComponent();

        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        VersionText.Text = version != null ? $"GUI  ·  v{version}" : "GUI";

        // First run (no config.json yet): create one with sensible defaults, including "stay signed in"
        var firstRun = !File.Exists(ConfigPath);
        currentConfig = LoadConfig();
        if (firstRun) currentConfig.RememberPassword = true;

        AccountSettingsStore.LoadFromFile(AccountPath);
        StartLogFile();
        UpdateUIFromConfig();
        if (firstRun) { SaveConfig(); }
        if (!File.Exists(GuiSettingsPath)) GuiSettings.Save();

        DarkModeToggle.IsChecked = App.IsDark;

        logView = CollectionViewSource.GetDefaultView(logLines);
        logView.Filter = LogFilter;
        LogList.ItemsSource = logView;
        ActivityList.ItemsSource = activity;

        Steam3Session.UIDispatcher = Dispatcher;
        Logger.OnLogMessage += Logger_OnLogMessage;
        Steam3Session.OnQrCodeGenerated += Steam3Session_OnQrCodeGenerated;
        Steam3Session.AuthCodePrompt = PromptForAuthCodeAsync;   // Steam Guard code dialog (there is no console)

        UsernameBox.TextChanged += (_, _) => UpdateSessionInfo();
        QrToggle.Checked += (_, _) => UpdateAccountChip();
        QrToggle.Unchecked += (_, _) => UpdateAccountChip();
        StaySignedInToggle.Checked += (_, _) => UpdateSessionInfo();
        StaySignedInToggle.Unchecked += (_, _) => UpdateSessionInfo();

        uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        uiTimer.Tick += UiTimer_Tick;
        uiTimer.Start();

        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        Loaded += (_, _) => { loading = false; RefreshLibrary(); UpdateProgress(); };
        Closing += (_, _) => { try { UpdateConfigFromUI(); SaveConfig(); GuiSettings.Save(); } catch { } };

        AppLog(firstRun ? "Welcome! Created config.json - enter your Steam account in Settings to get started." : "Depot Dumper GUI ready.");
    }

    private static string ConfigPath => AppPaths.ConfigFile;
    private static string AccountPath => AppPaths.AccountFile;
    private static string GuiSettingsPath => AppPaths.GuiSettingsFile;

    /// <summary>Send startup logging to dumps\logs instead of a stray DepotDumper.log next to the exe.</summary>
    private void StartLogFile()
    {
        try
        {
            Enum.TryParse<LogLevel>(currentConfig.LogLevel, true, out var level);
            Logger.Initialize(Path.Combine(DumpDir(), "logs", "depotdumper_gui.log"), level, toConsole: false, toFile: true);
        }
        catch { /* logging is best effort */ }
    }

    private bool HasSavedSession(string? username) =>
        !string.IsNullOrWhiteSpace(username) && AccountSettingsStore.Instance.LoginTokens.ContainsKey(username.Trim());

    private void UpdateSessionInfo()
    {
        var user = UsernameBox.Text.Trim();
        var saved = StaySignedInToggle.IsChecked == true && HasSavedSession(user);
        SessionText.Text = saved
            ? "A login is saved for this account - no password or Steam Guard code needed."
            : "No saved login yet. Enter your password (and Steam Guard code when asked) once.";
        ForgetSessionButton.Visibility = HasSavedSession(user) ? Visibility.Visible : Visibility.Collapsed;
        UpdateAccountChip();
    }

    private void ForgetSession_Click(object sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text.Trim();
        AccountSettingsStore.Forget(user);
        AppLog($"Forgot the saved login for {user}. You'll be asked for the password and code next time.");
        UpdateSessionInfo();
    }

    // ============================================================ account switcher

    private HashSet<string> tokensBeforeRun = new();

    private void AccountChip_Click(object sender, MouseButtonEventArgs e)
    {
        RebuildAccountList();
        AccountPopup.IsOpen = true;
    }

    /// <summary>Lists every account with a saved login (plus the one currently typed in).</summary>
    private void RebuildAccountList()
    {
        AccountList.Children.Clear();
        var current = UsernameBox.Text.Trim();
        var names = AccountSettingsStore.Instance.LoginTokens.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (current.Length > 0 && !names.Contains(current, StringComparer.OrdinalIgnoreCase)) names.Insert(0, current);

        if (names.Count == 0)
        {
            var none = new TextBlock { Text = "No accounts yet", Margin = new Thickness(10, 2, 10, 8) };
            none.SetResourceReference(StyleProperty, "Caption");
            AccountList.Children.Add(none);
            return;
        }

        foreach (var name in names)
        {
            var isCurrent = string.Equals(name, current, StringComparison.OrdinalIgnoreCase);

            var check = new TextBlock { Text = isCurrent ? "" : "", Width = 22, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(StyleProperty, "Icon");
            check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

            var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 };
            title.SetResourceReference(TextBlock.ForegroundProperty, isCurrent ? "TextBrush" : "MutedBrush");
            var sub = new TextBlock { Text = HasSavedSession(name) ? "signed in" : "needs password" };
            sub.SetResourceReference(StyleProperty, "Caption");

            var text = new StackPanel();
            text.Children.Add(title);
            text.Children.Add(sub);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(check);
            content.Children.Add(text);

            var row = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 8, 10, 8) };
            row.SetResourceReference(StyleProperty, "GhostButton");
            var picked = name;
            row.Click += (_, _) => { AccountPopup.IsOpen = false; SwitchAccount(picked); };
            AccountList.Children.Add(row);
        }
    }

    private void SwitchAccount(string name)
    {
        if (isRunning) { AppLog("Stop the current run before switching accounts.", "WARNING"); return; }
        if (string.Equals(name, UsernameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return;

        UsernameBox.Text = name;
        PasswordInput.Password = "";
        QrToggle.IsChecked = false;
        StaySignedInToggle.IsChecked = true;
        UpdateConfigFromUI();
        SaveConfig();
        UpdateSessionInfo();

        if (HasSavedSession(name)) AppLog($"Switched to {name} (saved login).");
        else
        {
            AppLog($"Switched to {name}. This account has no saved login - enter its password in Settings.", "WARNING");
            NavSettings.IsChecked = true;
            PasswordInput.Focus();
        }
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        if (isRunning) { AppLog("Stop the current run before adding an account.", "WARNING"); return; }
        UsernameBox.Text = "";
        PasswordInput.Password = "";
        QrToggle.IsChecked = false;
        StaySignedInToggle.IsChecked = true;
        NavSettings.IsChecked = true;
        UsernameBox.Focus();
        AppLog("Enter the new account's username and password (or use QR sign-in), then press Start dump.");
    }

    // ============================================================ config <-> UI

    private static ConfigFile LoadConfig()
    {
        try { return ConfigFile.Load(); }
        catch { return new ConfigFile(); }
    }

    private void UpdateUIFromConfig()
    {
        loading = true;
        var c = currentConfig;
        UsernameBox.Text = c.Username ?? "";
        PasswordInput.Password = c.Password ?? "";          // was never restored in the old GUI
        QrToggle.IsChecked = c.UseQrCode;
        StaySignedInToggle.IsChecked = c.RememberPassword;
        SavePasswordToggle.IsChecked = GuiSettings.Current.SavePassword;

        AppIdsBox.Text = c.AppIdsToProcess is { Count: > 0 } ? string.Join(", ", c.AppIdsToProcess) : "";
        ExcludedBox.Text = c.ExcludedAppIds is { Count: > 0 } ? string.Join(", ", c.ExcludedAppIds) : "";
        (string.Equals(c.BranchFilter, "public", StringComparison.OrdinalIgnoreCase) ? BranchPublic : BranchAll).IsChecked = true;

        DynamicToggle.IsChecked = c.DynamicConcurrency;
        DownloadManifestsToggle.IsChecked = c.DownloadManifests;
        KeepOldToggle.IsChecked = !c.DeleteOldManifests;
        HistoryToggle.IsChecked = c.DownloadHistoricalManifests;

        CollectAfterToggle.IsChecked = c.CollectAfterRun;
        CollectPublicToggle.IsChecked = c.CollectPublicOnly;
        CollectNoBetaToggle.IsChecked = c.CollectNoBeta;
        CollectLatestToggle.IsChecked = c.CollectLatestOnly;
        CollectDepotCacheToggle.IsChecked = c.CollectIncludeSteamDepotCache;

        MaxParallelDepotsBox.Text = c.MaxParallelDepots.ToString();
        MaxMemoryBox.Text = c.MaxMemoryGb > 0 ? c.MaxMemoryGb.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
        MaxDownloadsBox.Text = c.MaxDownloads.ToString();
        MaxConcurrentAppsBox.Text = c.MaxConcurrentApps.ToString();
        MaxServersBox.Text = c.MaxServers.ToString();
        CellIdBox.Text = c.CellID.ToString();
        LoginIdBox.Text = c.LoginID?.ToString() ?? "";

        DumpDirBox.Text = string.IsNullOrWhiteSpace(c.DumpDirectory) ? "dumps" : c.DumpDirectory;
        (c.LogLevel?.ToLowerInvariant() switch
        {
            "debug" => LogLevelDebug,
            "warning" => LogLevelWarning,
            "error" => LogLevelError,
            _ => LogLevelInfo,
        }).IsChecked = true;

        UpdateSessionInfo();
        PortableToggle.IsChecked = AppPaths.IsPortable;
        UpdateStorageInfo();
        loading = false;
    }

    private void UpdateConfigFromUI()
    {
        var c = currentConfig;
        c.Username = UsernameBox.Text.Trim();
        c.Password = PasswordInput.Password;
        c.UseQrCode = QrToggle.IsChecked == true;
        c.RememberPassword = StaySignedInToggle.IsChecked == true;
        GuiSettings.Current.SavePassword = SavePasswordToggle.IsChecked == true;

        c.AppIdsToProcess = ParseIds(AppIdsBox.Text);
        c.ExcludedAppIds = ParseIds(ExcludedBox.Text);
        c.BranchFilter = BranchPublic.IsChecked == true ? "public" : null;

        c.DynamicConcurrency = DynamicToggle.IsChecked == true;
        c.DownloadManifests = DownloadManifestsToggle.IsChecked == true;
        c.DeleteOldManifests = KeepOldToggle.IsChecked != true;
        c.DownloadHistoricalManifests = HistoryToggle.IsChecked == true;

        c.CollectAfterRun = CollectAfterToggle.IsChecked == true;
        c.CollectPublicOnly = CollectPublicToggle.IsChecked == true;
        c.CollectNoBeta = CollectNoBetaToggle.IsChecked == true;
        c.CollectLatestOnly = CollectLatestToggle.IsChecked == true;
        c.CollectIncludeSteamDepotCache = CollectDepotCacheToggle.IsChecked == true;

        if (int.TryParse(MaxDownloadsBox.Text, out var md) && md > 0) c.MaxDownloads = md;
        if (int.TryParse(MaxParallelDepotsBox.Text, out var mpd) && mpd > 0) c.MaxParallelDepots = Math.Min(mpd, 64);
        c.MaxMemoryGb = double.TryParse(MaxMemoryBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0 ? gb : 0;
        if (int.TryParse(MaxConcurrentAppsBox.Text, out var ma) && ma > 0) c.MaxConcurrentApps = ma;
        if (int.TryParse(MaxServersBox.Text, out var ms) && ms > 0) c.MaxServers = ms;
        if (int.TryParse(CellIdBox.Text, out var cell) && cell >= 0) c.CellID = cell;
        c.LoginID = uint.TryParse(LoginIdBox.Text, out var lid) ? lid : null;

        c.DumpDirectory = string.IsNullOrWhiteSpace(DumpDirBox.Text) ? "dumps" : DumpDirBox.Text.Trim();
        c.LogLevel = LogLevelDebug.IsChecked == true ? "Debug"
                   : LogLevelWarning.IsChecked == true ? "Warning"
                   : LogLevelError.IsChecked == true ? "Error" : "Info";
    }

    private static HashSet<uint> ParseIds(string text)
    {
        var set = new HashSet<uint>();
        foreach (var part in text.Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(part.Trim(), out var id)) set.Add(id);
        return set;
    }

    /// <summary>Writes config.json. The password is only written if "Save password" is on.</summary>
    private void SaveConfig()
    {
        var pw = currentConfig.Password;
        if (!GuiSettings.Current.SavePassword) currentConfig.Password = null;
        try { currentConfig.Save(); }
        catch (Exception ex) { AppLog($"Could not save settings: {ex.Message}", "ERROR"); }
        finally { currentConfig.Password = pw; }
    }

    private string DumpDir()
    {
        return AppPaths.ResolveDumpDir(currentConfig.DumpDirectory);
    }

    private void UpdateAccountChip()
    {
        var user = UsernameBox.Text.Trim();
        if (string.IsNullOrEmpty(user)) SidebarAccount.Text = QrToggle.IsChecked == true ? "QR code sign-in" : "No account set";
        else SidebarAccount.Text = HasSavedSession(user) ? $"{user}  ·  signed in" : user;
    }

    // ============================================================ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageLogs == null) return;   // fires during InitializeComponent before all pages exist
        ShowPage(sender == NavLibrary ? 1 : sender == NavSettings ? 2 : sender == NavLogs ? 3 : 0);
    }

    private void ShowPage(int index)
    {
        PageDashboard.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        (PageTitle.Text, PageSubtitle.Text) = index switch
        {
            1 => ("Manifest library", "Every manifest ID this app knows about, including old versions for downgrading."),
            2 => ("Settings", "Account, what to dump, collection and performance options."),
            3 => ("Logs", "Live output from the dumper."),
            _ => ("Dashboard", "Dump your Steam library's depot keys, luas and manifests."),
        };
        if (index == 1) RefreshLibrary();
    }

    // ============================================================ run / stop

    private bool shuttingDown;   // Stop was pressed: waiting for in-flight work to finish

    private void SetRunState(bool running)
    {
        isRunning = running;
        if (!running) shuttingDown = false;
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running && !shuttingDown ? Visibility.Visible : Visibility.Collapsed;
        ForceButton.Visibility = running && shuttingDown ? Visibility.Visible : Visibility.Collapsed;
        ShutdownBanner.Visibility = running && shuttingDown ? Visibility.Visible : Visibility.Collapsed;

        var (text, brush) = !running ? ("Idle", "FaintBrush") : shuttingDown ? ("Shutting down", "WarnBrush") : ("Running", "AccentBrush");
        StatusText.Text = text;
        SidebarStatus.Text = text;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        SidebarDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (isRunning || toolBusy) return;

        UpdateConfigFromUI();

        // A saved login means no password, code or QR is needed: the dumper just signs in.
        bool hasSession = currentConfig.RememberPassword && HasSavedSession(currentConfig.Username);
        if (!hasSession && !currentConfig.UseQrCode && (string.IsNullOrWhiteSpace(currentConfig.Username) || string.IsNullOrEmpty(currentConfig.Password)))
        {
            NavSettings.IsChecked = true;
            MessageBox.Show(this, "Enter your Steam username and password (or turn on QR code sign-in) in Settings first.",
                "Depot Dumper GUI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var wantQr = currentConfig.UseQrCode;
        if (hasSession) currentConfig.UseQrCode = false;      // use the saved login instead of showing a QR code again
        SaveConfig();
        currentConfig.UseQrCode = wantQr;

        var args = new List<string>();
        if (currentConfig.UseQrCode && !hasSession) args.Add("-qr");
        if (!string.IsNullOrWhiteSpace(currentConfig.Username)) { args.Add("-username"); args.Add(currentConfig.Username); }
        if (!hasSession && !string.IsNullOrEmpty(currentConfig.Password)) { args.Add("-password"); args.Add(currentConfig.Password); }
        if (hasSession) AppLog($"Signing in as {currentConfig.Username} with the saved login.");

        shuttingDown = false;
        tokensBeforeRun = new HashSet<string>(AccountSettingsStore.Instance.LoginTokens.Keys);
        runStart = DateTime.Now;
        SetRunState(true);
        ProgressText.Text = "Connecting to Steam…";
        AppLog("Starting dump…");

        try
        {
            var code = await Task.Run(() => Program.MainAsync(args.ToArray()));
            AppLog(DepotDumper.StopRequested ? "Dump stopped." : code == 0 ? "Dump finished." : $"Dump ended with errors (exit code {code}). See the log.",
                   code == 0 ? "INFO" : "WARNING");
            ProgressText.Text = DepotDumper.StopRequested ? "Stopped. Anything already dumped was kept." : code == 0 ? "Finished." : "Finished with errors - check the Logs page.";
        }
        catch (Exception ex)
        {
            AppLog($"Dump failed: {ex.Message}", "ERROR");
            ProgressText.Text = "Failed - check the Logs page.";
            MessageBox.Show(this, ex.Message, "Dump failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunState(false);
            AfterRunSessionCheck(hasSession);
            UpdateProgress();
            RefreshLibrary();
        }
    }

    /// <summary>After a QR/password login the token is saved; remember the account name so the next run signs in silently.</summary>
    private void AfterRunSessionCheck(bool usedSession)
    {
        try
        {
            // a brand-new login (e.g. via QR) is saved under the Steam account name: adopt it as the current account
            var added = AccountSettingsStore.Instance.LoginTokens.Keys.Where(k => !tokensBeforeRun.Contains(k)).ToList();
            if (added.Count == 1 && (string.IsNullOrWhiteSpace(UsernameBox.Text) || QrToggle.IsChecked == true))
            {
                UsernameBox.Text = added[0];
                QrToggle.IsChecked = false;
                AppLog($"Signed in as {added[0]}. Your login is saved, so next time it signs in automatically.");
            }
            else if (usedSession && !HasSavedSession(currentConfig.Username))
            {
                AppLog("The saved login was rejected or expired. Enter your password in Settings and start again.", "WARNING");
                NavSettings.IsChecked = true;
            }
            UpdateSessionInfo();
        }
        catch { /* cosmetic */ }
    }

    /// <summary>Graceful stop: the dumper finishes what's in flight. The Force shutdown button appears straight away.</summary>
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!isRunning || shuttingDown) return;
        shuttingDown = true;
        DepotDumper.RequestStop();
        SetRunState(true);
        ProgressText.Text = "Finishing the downloads already in flight — this takes a moment.";
        AppLog("Graceful shutdown started. Force shutdown is available if it takes too long.", "WARNING");
    }

    private void ForceButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Quit immediately? Downloads in progress are abandoned. Anything already saved is kept, " +
            "and a half-written manifest is detected and re-downloaded next run.", "Force shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try { ManifestLedger.SaveToFile(); AccountSettingsStore.Save(); } catch { }
        Environment.Exit(1);
    }

    // ============================================================ live progress

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        FlushLog();
        UpdateProgress();
        if (++tick % 5 == 0 && (PageLibrary.Visibility == Visibility.Visible || isRunning)) UpdateLibraryStats();
        if (isRunning && tick % 40 == 0) RefreshTotals();     // the totals grow as new files land on disk
    }

    private void UpdateProgress()
    {
        var s = StatisticsTracker.GetLive();

        // Big numbers are totals from what's already on disk (they only ever grow); captions show this run's progress.
        StatAppsValue.Text = totals.Apps.ToString("N0");
        StatAppsCaption.Text = isRunning
            ? (s.PlannedApps > 0 ? $"{s.AppsDone} of {s.PlannedApps} done this run" : $"{s.AppsStarted} started this run")
                + (string.IsNullOrEmpty(s.CurrentApp) ? "" : $"  ·  {s.CurrentApp}")
            : "apps in your dumps folder";
        StatDepotsValue.Text = totals.Depots.ToString("N0");
        StatDepotsCaption.Text = isRunning || s.Depots > 0 ? $"{s.DepotsDone} finished this run" : "depots with a saved key";
        StatManifestsValue.Text = totals.Manifests.ToString("N0");
        StatManifestsCaption.Text = s.Manifests > 0 ? $"+{s.ManifestsNew} new  ·  {s.ManifestsReused} reused  ·  {s.ManifestsFailed} failed" : "unique manifests on disk";

        if (isRunning)
        {
            var sp = Throttle.Snapshot();
            SpeedText.Visibility = Visibility.Visible;
            SpeedText.Text = sp.Dynamic
                ? $"Speed: {sp.InFlight} depots working, {sp.Waiting} queued  ·  limit {sp.Workers} of {sp.MaxWorkers}  ·  CPU {sp.CpuPercent}%  ·  RAM {sp.MemoryPercent}% ({sp.FreeMemoryMb / 1024.0:0.0} GB free)  ·  app {sp.ProcessMemoryMb / 1024.0:0.0} GB" +
                  (sp.MemoryLimitMb > 0 ? $" of {sp.MemoryLimitMb / 1024.0:0.#} GB limit" : "") +
                  (sp.RateLimitHits > 0 ? $"  ·  {sp.RateLimitHits} rate-limit signal(s)" : "") + $"\nLast adjustment: {sp.LastAction}"
                : "Speed: dynamic speed is off - one depot at a time";
            var elapsed = DateTime.Now - runStart;
            ElapsedText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
            if (s.PlannedApps > 0)
            {
                RunProgress.IsIndeterminate = false;
                RunProgress.Value = 100.0 * s.AppsDone / s.PlannedApps;
                if (!shuttingDown)
                    ProgressText.Text = $"Processing {s.CurrentApp}  ·  {s.AppsDone} of {s.PlannedApps} apps done  ·  {s.Errors} errors";
            }
            else if (!toolBusy)
            {
                RunProgress.IsIndeterminate = true;
            }
        }
        else if (!toolBusy)
        {
            SpeedText.Visibility = Visibility.Collapsed;
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = s.PlannedApps > 0 ? 100.0 * s.AppsDone / s.PlannedApps : 0;
            ElapsedText.Text = "";
        }
    }

    // ============================================================ logging

    private void AppLog(string message, string level = "INFO") => Enqueue(level, message);

    private void Logger_OnLogMessage(string level, string message) => Enqueue(level, message);

    private void Enqueue(string level, string message)
    {
        level = level.ToUpperInvariant();
        logQueue.Enqueue(new LogLine
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Level = level,
            Message = message,
            Severity = level switch { "DEBUG" => 0, "INFO" => 1, "WARNING" => 2, _ => 3 },
        });
    }

    private void FlushLog()
    {
        if (logQueue.IsEmpty) return;
        int n = 0;
        while (n < 500 && logQueue.TryDequeue(out var line))
        {
            logLines.Add(line);
            if (line.Severity >= 1 && !line.Message.StartsWith("[Track", StringComparison.Ordinal)) { activity.Add(line); }
            n++;
        }
        while (logLines.Count > 6000) logLines.RemoveAt(0);
        while (activity.Count > 60) activity.RemoveAt(0);

        if (activity.Count > 0) ActivityList.ScrollIntoView(activity[^1]);
        if (AutoScrollToggle.IsChecked == true && PageLogs.Visibility == Visibility.Visible && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private int MinSeverity => FilterError.IsChecked == true ? 3 : FilterWarn.IsChecked == true ? 2 : FilterInfo.IsChecked == true ? 1 : 0;

    private bool LogFilter(object o)
    {
        if (o is not LogLine l) return false;
        if (l.Severity < MinSeverity) return false;
        var q = LogSearch?.Text;
        return string.IsNullOrEmpty(q) || l.Message.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void LogFilter_Changed(object sender, RoutedEventArgs e) => logView?.Refresh();
    private void LogSearch_Changed(object sender, TextChangedEventArgs e) => logView?.Refresh();
    private void ClearLog_Click(object sender, RoutedEventArgs e) { logLines.Clear(); activity.Clear(); }

    // ============================================================ manifest library

    private void RefreshLibrary()
    {
        try
        {
            ManifestLedger.UseDirectory(DumpDir());
            var snapshot = ManifestLedger.Snapshot();
            ledgerRows = snapshot
                .OrderBy(x => x.AppId == 0)                 // rows with a known app first: they're the ones the downgrade helper can use
                .ThenBy(x => x.AppId)
                .ThenByDescending(x => x.ManifestId)
                .Select(x =>
                {
                    var status = x.Downloaded ? "Downloaded" : x.Unavailable ? "Unavailable" : "Pending";
                    var branches = string.Join(", ", x.Branches);
                    return new LedgerRow
                    {
                        Status = status, AppId = x.AppId, IsDownloaded = x.Downloaded,
                        App = x.AppId == 0 ? "—" : x.AppId.ToString(), Depot = x.DepotId, Manifest = x.ManifestId, Branches = branches,
                        FirstSeen = x.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Source = x.Source ?? "",
                        SearchText = $"{x.AppId} {x.DepotId} {x.ManifestId} {branches} {status}",
                    };
                }).ToList();
            ApplyLibraryFilter();
            UpdateLibraryStats();
            RefreshTotals();
        }
        catch (Exception ex) { AppLog($"Could not read the manifest library: {ex.Message}", "ERROR"); }
    }

    private void ApplyLibraryFilter()
    {
        var q = LibrarySearch.Text.Trim();
        IEnumerable<LedgerRow> rows = ledgerRows;
        if (q.Length > 0) rows = rows.Where(r => r.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = rows.Take(3000).ToList();
        LibraryGrid.ItemsSource = list;
        LibraryCount.Text = list.Count < (q.Length > 0 ? rows.Count() : ledgerRows.Count)
            ? $"showing first {list.Count:N0}"
            : $"{list.Count:N0} shown";
    }

    private void LibrarySearch_Changed(object sender, TextChangedEventArgs e) { if (LibraryGrid != null) ApplyLibraryFilter(); }

    private void UpdateLibraryStats()
    {
        var s = ManifestLedger.GetStats();
        StatHistoryValue.Text = s.Pending.ToString("N0");
        StatHistoryCaption.Text = s.Unavailable > 0
            ? $"known IDs not downloaded  ·  {s.Unavailable:N0} unavailable"
            : "known manifest IDs not downloaded yet";
        var unindexed = totals.Manifests - s.Downloaded;
        IndexHint.Visibility = unindexed > 10 ? Visibility.Visible : Visibility.Collapsed;
        IndexHint.Text = $"Your dumps folder has about {unindexed:N0} manifests this library hasn't indexed yet — press Scan local files to add them.";
        LibKnown.Text = s.Total.ToString("N0");
        LibDownloaded.Text = s.Downloaded.ToString("N0");
        LibPending.Text = s.Pending.ToString("N0");
        LibUnavailable.Text = s.Unavailable.ToString("N0");
    }

    // ---- totals shown on the dashboard, read from what's already on disk

    private LibraryTotals totals;
    private int totalsBusy;

    private async void RefreshTotals()
    {
        if (Interlocked.Exchange(ref totalsBusy, 1) == 1) return;
        try
        {
            var dir = DumpDir();
            ManifestLedger.UseDirectory(dir);
            totals = await Task.Run(() => LibraryStats.Compute(dir));
            UpdateProgress();
        }
        catch (Exception ex) { AppLog($"Could not count the existing dump: {ex.Message}", "WARNING"); }
        finally { Interlocked.Exchange(ref totalsBusy, 0); }
    }

    // ---- downgrade helper

    private const string DowngradeDefaultHint = "Select one or more downloaded manifests below (Ctrl / Shift to pick several), then copy the commands to install that version.";

    private void LibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var n = LibraryGrid.SelectedItems.Count;
        DowngradeHint.Text = n == 0 ? DowngradeDefaultHint : $"{n:N0} manifest{(n == 1 ? "" : "s")} selected.";
    }

    private List<DowngradeHelper.Item>? GetDowngradeSelection()
    {
        var rows = LibraryGrid.SelectedItems.Cast<LedgerRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more manifests in the table first (Ctrl / Shift to pick several).", "Downgrade helper", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var usable = rows.Where(r => r.IsDownloaded && r.AppId != 0).ToList();
        if (usable.Count < rows.Count)
            AppLog($"{rows.Count - usable.Count} selected row(s) skipped: only downloaded manifests with a known app ID can be used.", "WARNING");
        if (usable.Count == 0)
        {
            MessageBox.Show(this, "None of the selected rows can be used: they need to be downloaded and have an app ID.", "Downgrade helper", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        return usable.Select(r => new DowngradeHelper.Item(r.AppId, r.Depot, r.Manifest)).ToList();
    }

    private void CopyToClipboard(string text, string what)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); AppLog(what); return; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(60); }   // clipboard briefly locked by another app
        }
        AppLog("Could not access the clipboard.", "ERROR");
    }

    private void CopySteamConsole_Click(object sender, RoutedEventArgs e)
    {
        if (GetDowngradeSelection() is not { } items) return;
        CopyToClipboard(DowngradeHelper.SteamConsole(items),
            $"Copied {items.Count} Steam console command(s). Open steam://open/console in your browser or Run box, then paste.");
    }

    private void CopyDepotDownloader_Click(object sender, RoutedEventArgs e)
    {
        if (GetDowngradeSelection() is not { } items) return;
        CopyToClipboard(DowngradeHelper.DepotDownloader(items, UsernameBox.Text), $"Copied {items.Count} DepotDownloader command(s).");
    }

    private void SaveBat_Click(object sender, RoutedEventArgs e)
    {
        if (GetDowngradeSelection() is not { } items) return;
        var dialog = new SaveFileDialog { Title = "Save downgrade script", Filter = "Batch file (*.bat)|*.bat", FileName = "downgrade.bat", InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, DowngradeHelper.BatchFile(items, UsernameBox.Text));
            AppLog($"Saved {items.Count} download command(s) to {dialog.FileName}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ---- send manifests to Steam's depotcache

    private async void SendToSteam_Click(object sender, RoutedEventArgs e)
    {
        var dir = DumpDir();
        var dest = SteamIntegration.DepotCacheDir();
        if (dest == null)
        {
            MessageBox.Show(this, "Steam's install folder wasn't found in the registry, so there is nowhere to send the manifests.", "Send manifests to Steam", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var count = SteamIntegration.CountPooledManifests(dir);
        if (count == 0)
        {
            MessageBox.Show(this, "There are no pooled manifests yet. Press \"Collect luas & manifests now\" on the Dashboard first.", "Send manifests to Steam", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var running = SteamIntegration.IsSteamRunning() ? "\n\nSteam is running - restart it afterwards so it notices the new files." : "";
        var ok = MessageBox.Show(this, $"Copy {count:N0} manifests from dumps\\manifests into:\n{dest}\n\nExisting files are never overwritten.{running}",
            "Send manifests to Steam", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        await RunToolAsync("Sending manifests to Steam…", () => SteamIntegration.SendManifestsToDepotCache(dir, m => Logger.Info(m)).ToString());
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        UpdateConfigFromUI();
        SaveConfig();
    }

    // ============================================================ tools (collect / scan / import)

    private async Task RunToolAsync(string title, Func<string> work)
    {
        if (isRunning || toolBusy) { AppLog("Wait for the current job to finish first.", "WARNING"); return; }
        toolBusy = true;
        UpdateConfigFromUI();
        SaveConfig();
        currentConfig.ApplyToDepotDumperConfig();
        RunProgress.IsIndeterminate = true;
        ProgressText.Text = title;
        AppLog(title);
        try
        {
            var summary = await Task.Run(work);
            foreach (var line in summary.Split('\n')) AppLog(line.TrimEnd());
            ProgressText.Text = summary.Split('\n')[0];
        }
        catch (Exception ex)
        {
            AppLog($"{title} failed: {ex.Message}", "ERROR");
            ProgressText.Text = "Failed - check the Logs page.";
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            toolBusy = false;
            RunProgress.IsIndeterminate = false;
            RefreshLibrary();
        }
    }

    private async void CollectNow_Click(object sender, RoutedEventArgs e)
    {
        var dir = DumpDir();
        await RunToolAsync("Collecting luas and manifests…", () => Collector.Run(Program.BuildCollectOptions(dir), m => Logger.Info(m)).ToString());
    }

    private async void ScanLocal_Click(object sender, RoutedEventArgs e)
    {
        var dir = DumpDir();
        await RunToolAsync("Scanning local files for manifests…", () =>
        {
            var o = Program.BuildCollectOptions(dir, manifestsOnly: true);
            o.PublicOnly = false; o.NoBeta = false; o.LatestOnly = false; o.IncludeSteamDepotCache = true;
            return Collector.Run(o, m => Logger.Info(m)).ToString();
        });
    }

    private async void ImportIds_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import manifest IDs",
            Filter = "Text or CSV (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*",
            InitialDirectory = AppContext.BaseDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;
        var file = dialog.FileName;
        await RunToolAsync("Importing manifest IDs…", () =>
        {
            ManifestLedger.UseDirectory(DumpDir());
            var r = ManifestHistory.ImportIds(file);
            return $"Imported {r.Added:N0} new manifest IDs ({r.AlreadyKnown:N0} already known, {r.Invalid:N0} lines skipped).";
        });
    }

    private async void RetryUnavailable_Click(object sender, RoutedEventArgs e)
    {
        await RunToolAsync("Resetting unavailable manifests…", () =>
        {
            ManifestLedger.UseDirectory(DumpDir());
            var n = ManifestLedger.ResetUnavailable();
            return $"{n:N0} manifests will be retried on the next run.";
        });
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog($"Could not open {path}: {ex.Message}", "ERROR"); }
    }

    private void OpenDumps_Click(object sender, RoutedEventArgs e) => OpenFolder(DumpDir());
    private void OpenManifestPool_Click(object sender, RoutedEventArgs e) => OpenFolder(Collector.ManifestDir(DumpDir()));
    private void OpenLuaPool_Click(object sender, RoutedEventArgs e) => OpenFolder(Collector.LuaDir(DumpDir()));
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(DumpDir(), "logs"));

    // ============================================================ settings file actions

    // ---- storage: where settings and dumps live

    private void UpdateStorageInfo()
    {
        if (DumpDirResolved == null) return;
        DumpDirResolved.Text = "Full path: " + AppPaths.ResolveDumpDir(DumpDirBox.Text);
        SettingsLocationText.Text = "Stored in: " + AppPaths.SettingsDir;
        if (PortableToggle.IsChecked != AppPaths.IsPortable)
        {
            var was = loading; loading = true;
            PortableToggle.IsChecked = AppPaths.IsPortable;
            loading = was;
        }
    }

    private void DumpDirBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStorageInfo();

    private void PortableToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        bool want = PortableToggle.IsChecked == true;
        if (want == AppPaths.IsPortable) return;

        if (isRunning || toolBusy)
        {
            AppLog("Finish or stop the current job before changing where settings are stored.", "WARNING");
            UpdateStorageInfo();
            return;
        }

        try
        {
            UpdateConfigFromUI();
            // A relative dump folder (the default "dumps") would point somewhere else after the switch: pin it to where it is now.
            if (!Path.IsPathRooted(currentConfig.DumpDirectory ?? ""))
            {
                currentConfig.DumpDirectory = DumpDir();
                DumpDirBox.Text = currentConfig.DumpDirectory;
            }
            SaveConfig(); AccountSettingsStore.Save(); GuiSettings.Save();   // current state on disk first

            AppPaths.SwitchMode(want);

            AccountSettingsStore.LoadFromFile(AppPaths.AccountFile);          // later saves go to the new home
            SaveConfig(); AccountSettingsStore.Save(); GuiSettings.Save();
            AppLog(want ? $"Portable mode on: settings now live in {AppPaths.SettingsDir}."
                        : $"Portable mode off: settings now live in {AppPaths.SettingsDir}. The old copies next to the exe were left in place.");
        }
        catch (Exception ex)
        {
            AppLog($"Could not switch storage mode: {ex.Message}", "ERROR");
            MessageBox.Show(this, ex.Message, "Portable mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateStorageInfo();
    }

    private async void MoveDumps_Click(object sender, RoutedEventArgs e)
    {
        if (isRunning || toolBusy) { AppLog("Finish or stop the current job before moving the dumps.", "WARNING"); return; }

        var source = DumpDir();
        var dialog = new OpenFolderDialog { Title = "Choose an empty folder to move the dumps into" };
        if (dialog.ShowDialog(this) != true) return;
        var target = dialog.FolderName;

        if (!Directory.Exists(source) || !Directory.EnumerateFileSystemEntries(source).Any())
        {
            DumpDirBox.Text = target;                        // nothing to move: just switch
            UpdateConfigFromUI(); SaveConfig(); RefreshLibrary();
            AppLog($"Dump folder set to {target}.");
            return;
        }

        var ok = MessageBox.Show(this, $"Move everything in\n{source}\n\nto\n{target}\n\nOn the same drive this is instant; across drives it copies, verifies, then removes the original. Keep this window open until it finishes.",
            "Move dumps", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        toolBusy = true;
        RunProgress.IsIndeterminate = true;
        ProgressText.Text = "Moving the dumps folder…";
        AppLog($"Moving dumps from {source} to {target}...");
        try
        {
            var summary = await Task.Run(() =>
            {
                ManifestLedger.SaveToFile();
                return DumpsMover.Move(source, target, m => Logger.Info(m));
            });
            DumpDirBox.Text = target;
            UpdateConfigFromUI(); SaveConfig();
            currentConfig.ApplyToDepotDumperConfig();        // re-points the ledger at the new folder
            AppLog(summary);
            ProgressText.Text = summary;
        }
        catch (Exception ex)
        {
            AppLog($"Move failed: {ex.Message}", "ERROR");
            ProgressText.Text = "Move failed - the original folder was not deleted.";
            MessageBox.Show(this, ex.Message, "Move dumps", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            toolBusy = false;
            RunProgress.IsIndeterminate = false;
            StartLogFile();
            RefreshLibrary();
            UpdateStorageInfo();
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select dump directory", InitialDirectory = DumpDir() };
        if (dialog.ShowDialog(this) == true) DumpDirBox.Text = dialog.FolderName;
    }

    private void LoadConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import settings", Filter = "JSON (*.json)|*.json|All files (*.*)|*.*", InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            currentConfig = ConfigFile.Load(dialog.FileName);
            UpdateUIFromConfig();
            AppLog($"Settings imported from {dialog.FileName}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export settings", Filter = "JSON (*.json)|*.json", FileName = "config.json", InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            UpdateConfigFromUI();
            var pw = currentConfig.Password;
            if (!GuiSettings.Current.SavePassword) currentConfig.Password = null;
            try { currentConfig.Save(dialog.FileName); } finally { currentConfig.Password = pw; }
            AppLog($"Settings exported to {dialog.FileName}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ============================================================ theme

    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || DarkModeToggle == null) return;
        App.ApplyTheme(DarkModeToggle.IsChecked == true ? "Dark" : "Light");
        GuiSettings.Save();
        ApplyTitleBarTheme();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark/light title bar (and matching caption colour on Windows 11).</summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = App.IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
            if (TryFindResource("BgColor") is Color c)
            {
                int colorRef = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int));   // DWMWA_CAPTION_COLOR (Windows 11)
            }
        }
        catch { /* cosmetic only */ }
    }

    // ============================================================ Steam login helpers

    private void Steam3Session_OnQrCodeGenerated(string challengeUrl, byte[][] qrMatrix)
    {
        try
        {
            var qrWindow = new QrCodeWindow(challengeUrl, qrMatrix) { Owner = this };
            qrWindow.Show();   // non-modal so authentication can continue
        }
        catch (Exception ex) { AppLog($"Could not show the QR code: {ex.Message}", "ERROR"); }
    }

    private Task<string?> PromptForAuthCodeAsync(AuthPromptKind kind, string? email, bool previousCodeWasIncorrect)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            Activate();
            var dialog = new AuthCodeWindow(kind, email, previousCodeWasIncorrect) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Code : null;
        }).Task;
    }

    protected override void OnClosed(EventArgs e)
    {
        uiTimer.Stop();
        Logger.OnLogMessage -= Logger_OnLogMessage;
        Steam3Session.OnQrCodeGenerated -= Steam3Session_OnQrCodeGenerated;
        Steam3Session.AuthCodePrompt = null;
        base.OnClosed(e);
    }

}
