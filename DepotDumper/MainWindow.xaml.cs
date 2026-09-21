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
    public DateTime? Created { get; init; }
    public string CreatedText => Created?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string Source { get; init; } = "";
    public string SearchText { get; init; } = "";
    public string AppName { get; init; } = "";
    public uint ContentAppId { get; init; }
    /// <summary>"" for apps without DLC content; otherwise "Main game" or "DLC · name (id)". Shown as a second level under the app.</summary>
    public string SubGroup { get; init; } = "";
    /// <summary>Rows of one app are grouped under a collapsible header.</summary>
    public string Group => AppId == 0 ? "Unknown app" : string.IsNullOrEmpty(AppName) ? AppId.ToString() : $"{AppId}  ·  {AppName}";
}

/// <summary>"12" + "manifest|manifests" -> "12 manifests"; "1" -> "1 manifest".</summary>
public class CountLabelConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var n = values.Length > 0 && values[0] is int i ? i : 0;
        var words = (values.Length > 1 ? values[1] as string : null)?.Split('|') ?? new[] { "item", "items" };
        return $"{n:N0} {(n == 1 ? words[0] : words[words.Length > 1 ? 1 : 0])}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
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
    private readonly Queue<(DateTime At, int Done)> runSamples = new();   // recent progress, for the apps/min rate and time left

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

        UsernameBox.TextChanged += (_, _) => { UpdateSessionInfo(); UpdateStartButton(); };
        QrToggle.Checked += (_, _) => UpdateAccountChip();
        QrToggle.Unchecked += (_, _) => UpdateAccountChip();
        StaySignedInToggle.Checked += (_, _) => UpdateSessionInfo();
        StaySignedInToggle.Unchecked += (_, _) => UpdateSessionInfo();

        uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        uiTimer.Tick += UiTimer_Tick;
        uiTimer.Start();

        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        Loaded += (_, _) => UpdateStartButton();
        Loaded += (_, _) => { loading = false; RefreshLibrary(); UpdateProgress(); };
        Closing += (_, _) => { try { if (isRunning) RunCheckpoint.Flush(); UpdateConfigFromUI(); SaveConfig(); GuiSettings.Save(); } catch { } };

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
        ShowPage(sender == NavLibrary ? 1 : sender == NavSettings ? 2 : sender == NavLogs ? 3 : sender == NavLua ? 4 : 0);
    }

    private void ShowPage(int index)
    {
        PageDashboard.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageLua.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        (PageTitle.Text, PageSubtitle.Text) = index switch
        {
            1 => ("Manifest library", "Every manifest ID this app knows about, including old versions for downgrading."),
            2 => ("Settings", "Account, what to dump, collection and performance options."),
            3 => ("Logs", "Live output from the dumper."),
            4 => ("Lua library", "Browse, check and copy the luas in your pooled folder."),
            _ => ("Dashboard", "Dump your Steam library's depot keys, luas and manifests."),
        };
        if (index == 1) RefreshLibrary();
        if (index == 4) RefreshLuas();
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
        if (!running) PauseControl.Resume();
        PauseButton.Visibility = running && !shuttingDown ? Visibility.Visible : Visibility.Collapsed;
        var paused = running && PauseControl.IsPaused;
        PauseButtonText.Text = paused ? "Resume" : "Pause";
        PauseButtonIcon.Text = paused ? "" : "";
        ShutdownBanner.Visibility = running && shuttingDown ? Visibility.Visible : Visibility.Collapsed;

        var (text, brush) = !running ? ("Idle", "FaintBrush") : shuttingDown ? ("Shutting down", "WarnBrush") : paused ? ("Paused", "WarnBrush") : ("Running", "AccentBrush");
        StatusText.Text = text;
        SidebarStatus.Text = text;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        SidebarDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
    }

    /// <summary>The main button says "Resume" while an interrupted whole-library dump for this account can be continued.</summary>
    private void UpdateStartButton()
    {
        if (StartButtonText == null || isRunning) return;
        try
        {
            var user = (UsernameBox?.Text ?? currentConfig.Username ?? "").Trim();
            var pending = currentConfig.AppIdsToProcess.Count == 0
                ? RunCheckpoint.FindResumable(DumpDir(), user, RunCheckpoint.ScopeKey(currentConfig.BranchFilter, currentConfig.DownloadManifests)) : null;
            StartButtonText.Text = pending != null ? "Resume" : "Start dump";
            StartButton.ToolTip = pending != null ? $"Continue the interrupted dump: {pending.Done:N0} of {pending.Planned:N0} apps already finished" : null;
        }
        catch { StartButtonText.Text = "Start dump"; }
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
            Dialogs.Show(this, "Enter your Steam username and password (or turn on QR code sign-in) in Settings first.",
                "Depot Dumper GUI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // An interrupted whole-library dump can carry on where it stopped (apps it already finished are skipped)
        var resume = false;
        if (currentConfig.AppIdsToProcess.Count == 0 &&
            RunCheckpoint.FindResumable(DumpDir(), currentConfig.Username ?? "", RunCheckpoint.ScopeKey(currentConfig.BranchFilter, currentConfig.DownloadManifests)) is { } pending)
        {
            var answer = Dialogs.Choose(this, "Resume the interrupted dump?",
                $"The last dump for this account was interrupted after {pending.Done:N0} of {pending.Planned:N0} apps ({pending.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}).\n\n" +
                "Resume skips the apps already finished. Start over checks every app again.",
                "Resume", "Start over", "Cancel", MessageBoxImage.Question);
            if (answer == DialogChoice.Cancel) return;
            resume = answer == DialogChoice.Primary;
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
        if (resume) { args.Add("-resume"); AppLog("Resuming the interrupted dump: apps it already finished will be skipped."); }

        shuttingDown = false;
        runSamples.Clear();
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
            Dialogs.Show(this, ex.Message, "Dump failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunState(false);
            UpdateStartButton();
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
    private DateTime pausedAt;

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!isRunning || shuttingDown) return;
        if (PauseControl.IsPaused)
        {
            runStart += DateTime.Now - pausedAt;   // the clock and the rate ignore the time spent paused
            runSamples.Clear();
            PauseControl.Resume();
            AppLog("Resumed.");
        }
        else
        {
            pausedAt = DateTime.Now;
            PauseControl.Pause();
            AppLog("Paused: nothing new will start; work already running is finishing. Press Resume to continue.");
        }
        SetRunState(true);
    }

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
        var answer = Dialogs.Choose(this, "Force shutdown?", "Quit immediately? Downloads in progress are abandoned. Anything already saved is kept, " +
            "and a half-written manifest is detected and re-downloaded next run. The dump can be resumed afterwards.", "Quit now", "Keep waiting", null, MessageBoxImage.Warning, dangerPrimary: true);
        if (answer != DialogChoice.Primary) return;
        try { RunCheckpoint.Flush(); } catch { }
        try { ManifestLedger.SaveToFile(); AccountSettingsStore.Save(); } catch { }
        Environment.Exit(1);
    }

    // ============================================================ live progress

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        FlushLog();
        UpdateProgress();
        if (!isRunning && tick % 25 == 0) UpdateStartButton();
        if (++tick % 5 == 0 && (PageLibrary.Visibility == Visibility.Visible || isRunning)) UpdateLibraryStats();
        if (isRunning && tick % 40 == 0) RefreshTotals();     // the totals grow as new files land on disk
    }

    private static string FormatLeft(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{Math.Ceiling(t.TotalMinutes):0} min" : "under a minute";
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
                ? $"Speed: {sp.InFlight} depots working, {sp.Waiting} queued  ·  limit {sp.Workers} of {sp.MaxWorkers}  ·  Steam requests {sp.CallsInFlight}/{sp.CallLimit}  ·  CPU {sp.CpuPercent}%  ·  RAM {sp.MemoryPercent}% ({sp.FreeMemoryMb / 1024.0:0.0} GB free)  ·  app {sp.ProcessMemoryMb / 1024.0:0.0} GB" +
                  (sp.MemoryLimitMb > 0 ? $" of {sp.MemoryLimitMb / 1024.0:0.#} GB limit" : "") +
                  (sp.RateLimitHits > 0 ? $"  ·  {sp.RateLimitHits} rate-limit signal(s)" : "") + $"\nLast adjustment: {sp.LastAction}"
                : "Speed: dynamic speed is off - one depot at a time";
            var elapsed = DateTime.Now - runStart - (PauseControl.IsPaused ? DateTime.Now - pausedAt : TimeSpan.Zero);
            ElapsedText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
            if (s.PlannedApps > 0)
            {
                RunProgress.IsIndeterminate = false;
                var pct = 100.0 * s.AppsDone / s.PlannedApps;
                RunProgress.Value = pct;

                // Rate: blend of the last ~90 s and the whole run, counting only apps actually worked on (resumed ones are instant)
                var now = DateTime.UtcNow;
                var worked = s.AppsDone - s.AppsResumed;
                runSamples.Enqueue((now, worked));
                while (runSamples.Count > 1 && (now - runSamples.Peek().At).TotalSeconds > 90) runSamples.Dequeue();
                var minutes = Math.Max(elapsed.TotalMinutes, 1.0 / 60);
                var overall = worked / minutes;
                var first = runSamples.Peek();
                var span = (now - first.At).TotalMinutes;
                var recent = span > 0.25 ? (worked - first.Done) / span : overall;
                var perMin = worked >= 3 ? (recent + overall) / 2 : overall;
                var left = perMin > 0.01 && worked >= 3 ? TimeSpan.FromMinutes((s.PlannedApps - s.AppsDone) / perMin) : (TimeSpan?)null;

                ElapsedText.Text = $"{pct:0}%  ·  {ElapsedText.Text}";
                var rate = worked > 0 ? $"{perMin:0.#} apps/min  ·  {s.Manifests / minutes:0} manifests/min" : "starting up";
                SpeedText.Text = $"Rate: {rate}" + (s.AppsResumed > 0 ? $"  ·  {s.AppsResumed} apps skipped (already finished)" : "") + "\n" + SpeedText.Text;
                if (!shuttingDown && PauseControl.IsPaused)
                    ProgressText.Text = $"Paused  ·  {pct:0}%  ·  {s.AppsDone} of {s.PlannedApps} apps  ·  work already running is finishing; press Resume to continue";
                else if (!shuttingDown)
                    ProgressText.Text = $"{pct:0}%  ·  {s.AppsDone} of {s.PlannedApps} apps  ·  {s.CurrentApp}" +
                                        (left is { } l ? $"  ·  about {FormatLeft(l)} left" : "  ·  estimating time left…") + $"  ·  {s.Errors} errors";
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

    private string? datesBackfilledFor;

    /// <summary>Reads the creation date of manifests that predate date tracking, then re-sorts both libraries.</summary>
    private void StartDateBackfill()
    {
        var dump = DumpDir();
        if (string.Equals(datesBackfilledFor, dump, StringComparison.OrdinalIgnoreCase)) return;
        datesBackfilledFor = dump;
        _ = Task.Run(async () =>
        {
            try
            {
                var n = await ManifestAgeBackfill.RunAsync(dump);
                if (n > 0) Dispatcher.Invoke(() => { AppLog($"Read the creation date of {n:N0} manifests so versions can be ordered newest first."); RefreshLibrary(); if (PageLua.Visibility == Visibility.Visible) RefreshLuas(); });
            }
            catch (Exception ex) { Logger.Warning($"Could not read manifest dates: {ex.Message}"); }
        });
    }

    private void RefreshLibrary()
    {
        StartDateBackfill();
        try
        {
            ManifestLedger.UseDirectory(DumpDir());
            var snapshot = ManifestLedger.Snapshot();
            var dumpForNames = DumpDir();
            var appNames = new Dictionary<uint, string>();
            var appsWithDlc = snapshot.Where(x => x.ContentAppId != 0 && x.ContentAppId != x.AppId).Select(x => x.AppId).ToHashSet();
            var dlcNames = new Dictionary<(uint, uint), string>();
            foreach (var x in snapshot.Where(x => x.ContentAppId != 0 && x.ContentAppId != x.AppId).Select(x => (x.AppId, x.ContentAppId)).Distinct())
                dlcNames[x] = LuaLibrary.DlcNameFor(dumpForNames, x.AppId, x.ContentAppId);
            foreach (var id in snapshot.Select(x => x.AppId).Where(id => id != 0).Distinct()) appNames[id] = LuaLibrary.AppNameFor(dumpForNames, id);
            ledgerRows = snapshot
                .OrderBy(x => x.AppId == 0)                 // rows with a known app first: they're the ones the downgrade helper can use
                .ThenBy(x => x.AppId)
                .ThenBy(x => x.ContentAppId != 0 && x.ContentAppId != x.AppId ? 1 : 0)   // the base game before its DLCs
                .ThenBy(x => x.ContentAppId)
                .ThenByDescending(x => x.CreatedUtc ?? DateTime.MinValue)   // newest version first
                .ThenByDescending(x => x.FirstSeenUtc)
                .ThenByDescending(x => x.ManifestId)
                .Select(x =>
                {
                    var status = x.Downloaded ? "Downloaded" : x.Unavailable ? "Unavailable" : "Pending";
                    var branches = string.Join(", ", x.Branches);
                    return new LedgerRow
                    {
                        Status = status, AppId = x.AppId, AppName = appNames.TryGetValue(x.AppId, out var an) ? an : "", IsDownloaded = x.Downloaded,
                        ContentAppId = x.ContentAppId, Created = x.CreatedUtc,
                        SubGroup = !appsWithDlc.Contains(x.AppId) ? "" : x.ContentAppId != 0 && x.ContentAppId != x.AppId
                            ? $"DLC  ·  {(dlcNames.TryGetValue((x.AppId, x.ContentAppId), out var dn) && dn.Length > 0 ? dn : "DLC " + x.ContentAppId)}  ({x.ContentAppId})" : "Main game",
                        App = x.AppId == 0 ? "—" : x.AppId.ToString(), Depot = x.DepotId, Manifest = x.ManifestId, Branches = branches,
                        FirstSeen = x.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Source = x.Source ?? "",
                        SearchText = $"{x.AppId} {x.ContentAppId} {(dlcNames.TryGetValue((x.AppId, x.ContentAppId), out var sd) ? sd : "")} {(appNames.TryGetValue(x.AppId, out var sn) ? sn : "")} {x.DepotId} {x.ManifestId} {branches} {status}",
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
        libExpandAll = q.Length > 0;   // while searching, show the matches instead of hiding them in collapsed groups
        LibraryGrid.ItemsSource = GroupedByApp(list, twoLevels: true);
        LibraryGrid.UnselectAll();
        UpdateGroupHeaders();
        LibraryCount.Text = list.Count < (q.Length > 0 ? rows.Count() : ledgerRows.Count)
            ? $"showing first {list.Count:N0}"
            : $"{list.Count:N0} shown";
    }

    // ---- grouping: one collapsible header per app, its rows (manifests, or lua variants) underneath

    private readonly HashSet<string> libExpanded = new(), luaExpanded = new();
    private bool libExpandAll, luaExpandAll;

    private void UpdateGroupHeaders() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var grid in new[] { LibraryGrid, LuaGrid })
                if (grid != null) grid.HeadersVisibility = HasVisibleRow(grid) ? DataGridHeadersVisibility.Column : DataGridHeadersVisibility.None;
        }), System.Windows.Threading.DispatcherPriority.Loaded);

    private static bool HasVisibleRow(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is DataGridRow row && row.IsVisible && row.ActualHeight > 0) return true;
            if (HasVisibleRow(child)) return true;
        }
        return false;
    }

    private static System.Collections.IEnumerable GroupedByApp<T>(List<T> rows, bool twoLevels = false)
    {
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
        if (twoLevels) view.GroupDescriptions.Add(new PropertyGroupDescription("SubGroup"));   // base game / each DLC, under the app
        return view;
    }

    /// <summary>Expansion is remembered per header; a sub-header is keyed together with its app so equal names don't clash.</summary>
    private static string GroupKey(DependencyObject header, CollectionViewGroup group)
    {
        var self = FindAncestor<GroupItem>(header);
        var outer = self == null ? null : FindAncestor<GroupItem>(VisualTreeHelper.GetParent(self));
        return outer?.DataContext is CollectionViewGroup parent ? $"{parent.Name}/{group.Name}" : group.Name?.ToString() ?? "";
    }

    private void GroupHeader_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton header || header.DataContext is not CollectionViewGroup group) return;
        var isLua = ReferenceEquals(FindAncestor<DataGrid>(header), LuaGrid);
        header.IsChecked = (isLua ? luaExpandAll : libExpandAll) || (isLua ? luaExpanded : libExpanded).Contains(GroupKey(header, group));
    }

    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton header || header.DataContext is not CollectionViewGroup group) return;
        var grid = FindAncestor<DataGrid>(header);
        var set = ReferenceEquals(grid, LuaGrid) ? luaExpanded : libExpanded;
        var key = GroupKey(header, group);
        if (header.IsChecked == true)
        {
            set.Add(key);
        }
        else
        {
            set.Remove(key);
            if (grid != null) foreach (var row in group.Items) grid.SelectedItems.Remove(row);
        }
        AfterGroupToggle(grid);
    }

    /// <summary>WPF only sizes DataGrid columns once a row is on screen; with every group collapsed they stay at 0 width, so rows and headers of a group opened later are blank until this nudges a re-measure.</summary>
    private static void ReflowColumns(DataGrid grid)
    {
        foreach (var column in grid.Columns)
        {
            var width = column.Width;
            column.Width = new DataGridLength(width.IsAbsolute ? width.Value + 1 : 100);
            column.Width = width;
        }
        grid.InvalidateMeasure();
        grid.UpdateLayout();
    }

    private void AfterGroupToggle(DataGrid? grid)
    {
        if (grid != null) Dispatcher.BeginInvoke(new Action(() => ReflowColumns(grid)), System.Windows.Threading.DispatcherPriority.Loaded);
        UpdateGroupHeaders();
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
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
            Dialogs.Show(this, "Select one or more manifests in the table first (Ctrl / Shift to pick several).", "Downgrade helper", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var usable = rows.Where(r => r.IsDownloaded && r.AppId != 0).ToList();
        if (usable.Count < rows.Count)
            AppLog($"{rows.Count - usable.Count} selected row(s) skipped: only downloaded manifests with a known app ID can be used.", "WARNING");
        if (usable.Count == 0)
        {
            Dialogs.Show(this, "None of the selected rows can be used: they need to be downloaded and have an app ID.", "Downgrade helper", MessageBoxButton.OK, MessageBoxImage.Information);
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
        catch (Exception ex) { Dialogs.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool downloadingVersion;
    private CancellationTokenSource? downloadCts;

    private async void DownloadVersion_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (downloadingVersion)   // the same button turns into "Cancel download" while a download runs
        {
            downloadCts?.Cancel();
            button.Content = "Cancelling…";
            button.IsEnabled = false;
            return;
        }
        if (GetDowngradeSelection() is not { } items) return;
        var dialog = new OpenFolderDialog { Title = "Choose where to install this version (a folder per app is created inside). Pick the folder of an earlier download to update it in place." };
        if (dialog.ShowDialog(this) != true) return;

        downloadingVersion = true;
        downloadCts = new CancellationTokenSource();
        button.Content = "Cancel download";
        var dumpDir = DumpDir();
        var maxDownloads = int.TryParse(MaxDownloadsBox.Text, out var md) && md > 0 ? md : 8;
        var lastPct = -1;
        var startedAt = DateTime.UtcNow;
        try
        {
            var summary = await DepotInstaller.DownloadAsync(items, dumpDir, dialog.FolderName, maxDownloads, m => AppLog(m),
                (done, total) =>
                {
                    var pct = total == 0 ? 0 : (int)(done * 100 / total);
                    if (pct == lastPct) return;
                    lastPct = pct;
                    var secs = Math.Max(1, (DateTime.UtcNow - startedAt).TotalSeconds);
                    Dispatcher.BeginInvoke(() => DowngradeHint.Text = $"Downloading… {pct}%  ({done / 1048576:N0} of {total / 1048576:N0} MB)  ·  {done / 1048576.0 / secs:0.0} MB/s");
                }, downloadCts.Token);
            foreach (var s in summary.Skipped) AppLog($"Skipped {s}", "WARNING");
            var reusedText = summary.ReusedBytes > 0 ? $" {summary.ReusedBytes / 1048576:N0} MB was reused from files already in the folder." : "";
            AppLog($"Version download finished: {summary.DepotsDone} depot(s) done, {summary.DepotsFailed} failed, {summary.Skipped.Count} skipped.{reusedText} Files are in {dialog.FolderName}.");

            if (summary.StaleFiles.Count > 0)
            {
                var sample = string.Join("\n", summary.StaleFiles.Take(8).Select(f => "  " + System.IO.Path.GetFileName(f)));
                var ask = Dialogs.Choose(this, "Remove files from the previous version?",
                    $"{summary.StaleFiles.Count:N0} file(s) from the version that was installed before are not part of this version:\n\n{sample}" +
                    (summary.StaleFiles.Count > 8 ? "\n  …" : "") + "\n\nDelete them so the folder matches this version exactly?",
                    "Delete them", "Keep them", null, MessageBoxImage.Question);
                if (ask == DialogChoice.Primary) AppLog($"Deleted {DepotInstaller.DeleteStale(summary.StaleFiles):N0} file(s) from the previous version.");
                else AppLog($"Kept {summary.StaleFiles.Count:N0} file(s) from the previous version.");
            }
            if (summary.DepotsDone > 0) System.Diagnostics.Process.Start("explorer.exe", dialog.FolderName);
        }
        catch (OperationCanceledException)
        {
            AppLog("Download cancelled. What was already downloaded is kept; run it again on the same folder to carry on.", "WARNING");
        }
        catch (Exception ex)
        {
            AppLog($"Version download failed: {ex.Message}", "ERROR");
            Dialogs.Show(this, ex.Message, "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            downloadingVersion = false;
            downloadCts?.Dispose();
            downloadCts = null;
            button.Content = "Download version…";
            button.IsEnabled = true;
            DowngradeHint.Text = DowngradeDefaultHint;
        }
    }
    // ---- export keys / verify a folder

    private async void ExportKeys_Click(object sender, RoutedEventArgs e)
    {
        var dir = DumpDir();
        var dialog = new SaveFileDialog { Title = "Export all depot keys", Filter = "Depot keys (*.keys)|*.keys|Text file (*.txt)|*.txt", FileName = "steam.keys", InitialDirectory = Directory.Exists(dir) ? dir : AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var result = await Task.Run(() => KeyExport.Run(dir, dialog.FileName));
            AppLog($"Exported {result.Keys:N0} depot keys from {result.Apps:N0} apps to {dialog.FileName}" +
                   (result.Conflicts > 0 ? $" ({result.Conflicts} depot(s) had differing keys in different files; the first was kept)." : "."));
            if (result.Keys == 0) Dialogs.Show(this, "No depot keys were found in your dumps folder yet.", "Export all keys", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportTokens_Click(object sender, RoutedEventArgs e)
    {
        var dir = DumpDir();
        var have = AccessTokens.Count(dir);
        if (have.Apps + have.Packages == 0)
        {
            Dialogs.Show(this, "No access tokens are saved yet. They are collected while a dump runs, so run a dump first.", "Export access tokens", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Choose a folder for app.tokens and package.tokens" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var t = AccessTokens.Export(dir, dialog.FolderName);
            AppLog($"Exported {t.Apps:N0} app tokens and {t.Packages:N0} package tokens to {dialog.FolderName} (app.tokens, package.tokens).");
        }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool verifying;

    private async void VerifyFolder_Click(object sender, RoutedEventArgs e)
    {
        if (verifying) return;
        if (GetDowngradeSelection() is not { } items) return;
        if (items.Count != 1)
        {
            Dialogs.Show(this, "Select exactly one manifest (one depot) to check a folder against.", "Verify folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var resolved = DowngradeHelper.Resolve(items, DumpDir());
        if (resolved.Ready.Count == 0)
        {
            Dialogs.Show(this, "The depot key or the manifest file for this row isn't in your dumps, so it can't be checked.", "Verify folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Choose the game folder to check against this manifest" };
        if (dialog.ShowDialog(this) != true) return;

        var (item, key, manifestPath) = resolved.Ready[0];
        verifying = true;
        var button = (Button)sender;
        button.IsEnabled = false;
        var lastPct = -1;
        try
        {
            AppLog($"Checking {dialog.FolderName} against depot {item.DepotId} manifest {item.ManifestId}...");
            var r = await Task.Run(() => FolderVerifier.Verify(manifestPath, key, dialog.FolderName, (done, total) =>
            {
                var pct = total == 0 ? 100 : done * 100 / total;
                if (pct == lastPct) return;
                lastPct = pct;
                Dispatcher.BeginInvoke(() => DowngradeHint.Text = $"Checking files… {pct}%");
            }));
            var problems = r.Missing.Count + r.WrongSize.Count + r.Mismatched.Count;
            foreach (var f in r.Missing.Take(25)) AppLog($"Missing: {f}", "WARNING");
            foreach (var f in r.WrongSize.Take(25)) AppLog($"Wrong size: {f}", "WARNING");
            foreach (var f in r.Mismatched.Take(25)) AppLog($"Different contents: {f}", "WARNING");
            var summary = $"{r.Good:N0} of {r.Files:N0} files match." + (problems == 0 ? " This folder is an exact copy of that version."
                : $" {r.Missing.Count:N0} missing, {r.WrongSize.Count:N0} wrong size, {r.Mismatched.Count:N0} with different contents." + (problems > 75 ? " (Only the first 25 of each are listed in the log.)" : ""));
            AppLog(summary);
            Dialogs.Show(this, summary, "Verify folder", MessageBoxButton.OK, problems == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLog($"Verify failed: {ex.Message}", "ERROR");
            Dialogs.Show(this, ex.Message, "Verify failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            verifying = false;
            button.IsEnabled = true;
            DowngradeHint.Text = DowngradeDefaultHint;
        }
    }

    // ---- right-click menus

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row || row.IsSelected) return;   // a row that is already part of the selection keeps the whole selection
        var grid = FindAncestor<DataGrid>(row);
        if (grid == null) return;
        grid.SelectedItems.Clear();
        row.IsSelected = true;
    }

    private void ShowInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { AppLog($"Could not open Explorer: {ex.Message}", "ERROR"); }
    }

    private string? ManifestPathOf(LedgerRow row) =>
        row.IsDownloaded ? DowngradeHelper.FindManifestFile(DumpDir(), row.AppId, row.Depot, row.Manifest) : null;

    private void LibShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItems.Count == 0 || LibraryGrid.SelectedItems[0] is not LedgerRow row) return;
        var path = ManifestPathOf(row);
        if (path == null) Dialogs.Show(this, "That manifest isn't downloaded, so there is no file to show.", "Show manifest in Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        else ShowInExplorer(path);
    }

    private void LibCopyManifestId_Click(object sender, RoutedEventArgs e)
    {
        var ids = LibraryGrid.SelectedItems.OfType<LedgerRow>().Select(r => r.Manifest.ToString()).ToList();
        if (ids.Count > 0) CopyToClipboard(string.Join(Environment.NewLine, ids), ids.Count == 1 ? "Copied the manifest ID." : $"Copied {ids.Count} manifest IDs.");
    }

    private void LibCopyDepotId_Click(object sender, RoutedEventArgs e)
    {
        var ids = LibraryGrid.SelectedItems.OfType<LedgerRow>().Select(r => r.Depot.ToString()).Distinct().ToList();
        if (ids.Count > 0) CopyToClipboard(string.Join(Environment.NewLine, ids), ids.Count == 1 ? "Copied the depot ID." : $"Copied {ids.Count} depot IDs.");
    }

    private void LuaOpen_Click(object sender, RoutedEventArgs e)
    {
        if (LuaGrid.SelectedItems.Count == 0 || LuaGrid.SelectedItems[0] is not LuaEntry entry) return;
        try { Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Show(this, $"Windows could not open this file with a default program:\n{ex.Message}", "Open lua", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void LuaShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (LuaGrid.SelectedItems.Count > 0 && LuaGrid.SelectedItems[0] is LuaEntry entry) ShowInExplorer(entry.Path);
    }

    private void LuaCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = LuaGrid.SelectedItems.OfType<LuaEntry>().Select(r => r.Path).ToList();
        if (paths.Count > 0) CopyToClipboard(string.Join(Environment.NewLine, paths), paths.Count == 1 ? "Copied the file path." : $"Copied {paths.Count} file paths.");
    }

    // ---- lua library
    private List<LuaEntry> luaRows = new();
    private bool luaLoading;

    private async void RefreshLuas()
    {
        if (luaLoading) return;
        luaLoading = true;
        try
        {
            var dir = DumpDir();
            luaRows = await Task.Run(() => LuaLibrary.Load(dir));
            ApplyLuaFilter();
        }
        catch (Exception ex) { AppLog($"Could not read the lua library: {ex.Message}", "ERROR"); }
        finally { luaLoading = false; }
    }

    private void ApplyLuaFilter()
    {
        var q = LuaSearch.Text.Trim();
        IEnumerable<LuaEntry> rows = luaRows;
        if (q.Length > 0) rows = rows.Where(r => r.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = rows.ToList();
        luaExpandAll = q.Length > 0;
        LuaGrid.ItemsSource = GroupedByApp(list);
        LuaGrid.UnselectAll();
        UpdateGroupHeaders();
        LuaCount.Text = luaRows.Count == 0 ? "no luas yet" : $"{list.Count:N0} of {luaRows.Count:N0}";
    }

    private void LuaRefresh_Click(object sender, RoutedEventArgs e) => RefreshLuas();
    private void LuaSearch_Changed(object sender, TextChangedEventArgs e) { if (LuaGrid != null) ApplyLuaFilter(); }

    private void LuaGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LuaGrid.SelectedItems.Count == 1 && LuaGrid.SelectedItem is LuaEntry entry)
        {
            LuaViewerTitle.Text = entry.FileName;
            try { LuaViewer.Text = File.ReadAllText(entry.Path); }
            catch (Exception ex) { LuaViewer.Text = $"Could not read the file: {ex.Message}"; }
        }
        else
        {
            LuaViewerTitle.Text = LuaGrid.SelectedItems.Count == 0 ? "Select a lua to preview it" : $"{LuaGrid.SelectedItems.Count} luas selected";
            LuaViewer.Text = "";
        }
    }

    private void LuaCopy_Click(object sender, RoutedEventArgs e)
    {
        var selected = LuaGrid.SelectedItems.Cast<LuaEntry>().ToList();
        if (selected.Count == 0) { Dialogs.Show(this, "Select one or more luas first.", "Lua library", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var text = string.Join(Environment.NewLine + Environment.NewLine, selected.Select(s => File.ReadAllText(s.Path).TrimEnd()));
            CopyToClipboard(text, selected.Count == 1 ? $"Copied {selected[0].FileName}." : $"Copied {selected.Count} luas.");
        }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LuaSave_Click(object sender, RoutedEventArgs e)
    {
        var selected = LuaGrid.SelectedItems.Cast<LuaEntry>().ToList();
        if (selected.Count == 0) { Dialogs.Show(this, "Select one or more luas first.", "Lua library", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new OpenFolderDialog { Title = "Choose a folder to save copies of the selected luas into" };
        if (dialog.ShowDialog(this) != true) return;
        int saved = 0, skipped = 0;
        foreach (var s in selected)
        {
            try
            {
                var target = System.IO.Path.Combine(dialog.FolderName, s.FileName);
                if (File.Exists(target)) { skipped++; continue; }   // never overwrite something already there
                File.Copy(s.Path, target);
                saved++;
            }
            catch (Exception ex) { AppLog($"Could not copy {s.FileName}: {ex.Message}", "ERROR"); }
        }
        AppLog($"Saved {saved} lua(s) to {dialog.FolderName}" + (skipped > 0 ? $" ({skipped} already existed and were left alone)." : "."));
    }

    // ---- send manifests to Steam's depotcache

    private async void SendToSteam_Click(object sender, RoutedEventArgs e)
    {
        var dir = DumpDir();
        var dest = SteamIntegration.DepotCacheDir();
        if (dest == null)
        {
            Dialogs.Show(this, "Steam's install folder wasn't found in the registry, so there is nowhere to send the manifests.", "Send manifests to Steam", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var count = SteamIntegration.CountPooledManifests(dir);
        if (count == 0)
        {
            Dialogs.Show(this, "There are no pooled manifests yet. Press \"Collect luas & manifests now\" on the Dashboard first.", "Send manifests to Steam", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var running = SteamIntegration.IsSteamRunning() ? "\n\nSteam is running - restart it afterwards so it notices the new files." : "";
        var ok = Dialogs.Show(this, $"Copy {count:N0} manifests from dumps\\manifests into:\n{dest}\n\nExisting files are never overwritten.{running}",
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
            Dialogs.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
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
            Dialogs.Show(this, ex.Message, "Portable mode", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var ok = Dialogs.Show(this, $"Move everything in\n{source}\n\nto\n{target}\n\nOn the same drive this is instant; across drives it copies, verifies, then removes the original. Keep this window open until it finishes.",
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
            Dialogs.Show(this, ex.Message, "Move dumps", MessageBoxButton.OK, MessageBoxImage.Error);
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
        catch (Exception ex) { Dialogs.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
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
        catch (Exception ex) { Dialogs.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ============================================================ theme

    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || DarkModeToggle == null) return;
        App.ApplyTheme(DarkModeToggle.IsChecked == true ? "Dark" : "Light");
        GuiSettings.Save();
        ApplyTitleBarTheme();
    }

    /// <summary>Dark/light title bar (and matching caption colour on Windows 11).</summary>
    private void ApplyTitleBarTheme() => WindowTheme.Apply(this);

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
