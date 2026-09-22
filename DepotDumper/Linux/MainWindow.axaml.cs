using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace DepotDumper.Linux;

/// <summary>
/// The small first cut of the Linux GUI: sign in, start or stop a whole-library dump, watch live progress and the log.
/// Reuses exactly the same engine as the Windows app and the command line (Program.MainAsync, Steam3Session, Throttle,
/// StatisticsTracker) - only the window is new.
/// </summary>
public partial class MainWindow : Window
{
    private ConfigFile currentConfig = null!;
    private bool isRunning;
    private bool shuttingDown;
    private readonly ConcurrentQueue<(string Level, string Message)> logQueue = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private HashSet<string> tokensBeforeRun = new();

    public MainWindow()
    {
        InitializeComponent();

        currentConfig = SafeLoadConfig();
        AccountSettingsStore.LoadFromFile(AppPaths.AccountFile);
        Logger.Initialize(null, LogLevel.Info, toConsole: false, toFile: false);   // pointed at the real dump folder once we know it

        UsernameBox.Text = currentConfig.Username ?? "";
        PasswordBox.Text = currentConfig.Password ?? "";
        RememberBox.IsChecked = currentConfig.RememberPassword;
        QrBox.IsChecked = currentConfig.UseQrCode;
        HistoryToggle.IsChecked = currentConfig.DownloadHistoricalManifests;

        Logger.OnLogMessage += (level, message) => logQueue.Enqueue((level, message));
        Steam3Session.AuthCodePrompt = PromptForAuthCodeAsync;
        Steam3Session.OnQrCodeGenerated += (url, matrix) => Dispatcher.UIThread.Post(() => ShowQr(matrix));
        Steam3Session.OnLoginSuccess += () => Dispatcher.UIThread.Post(() => QrText.IsVisible = false);

        timer.Tick += (_, _) => { FlushLog(); UpdateProgress(); };
        timer.Start();

        Closing += (_, _) => { try { if (isRunning) RunCheckpoint.Flush(); SaveConfigFromUi(); if (PageSettings.IsVisible) SaveSettingsFromUi(); } catch { } };

        AppLog("Depot Dumper (Linux) ready. This is a first cut: dumping, resume and pause work; the manifest/lua libraries and downgrade tools are Windows-only for now.");
    }

    private static ConfigFile SafeLoadConfig() { try { return ConfigFile.Load(); } catch { return new ConfigFile(); } }

    // ============================================================ navigation (sidebar)

    /// <summary>(nav button, content control, title, subtitle) - Dashboard is the only one with real content so far.</summary>
    private IEnumerable<(RadioButton Nav, Control Page, string Title, string Subtitle)> NavPages() => new (RadioButton, Control, string, string)[]
    {
        (NavDashboard, PageDashboard, "Dashboard", "Dump your Steam library's depot keys, luas and manifests."),
        (NavLibrary, PageLibrary, "Manifest library", "Every manifest ID this app knows about, including old versions for downgrading."),
        (NavLua, PageLua, "Lua library", "Browse, check and copy the luas in your pooled folder."),
        (NavIndex, PageIndex, "Game index", "Ask questions about your own dumps with SQL."),
        (NavSettings, PageSettings, "Settings", "Account, what to dump, collection and performance options."),
        (NavLogs, PageLogs, "Logs", "Live output from the dumper."),
    };

    private void Nav_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var leavingSettings = PageSettings.IsVisible;
        foreach (var (nav, page, title, subtitle) in NavPages())
        {
            if (nav != sender) continue;
            if (leavingSettings && nav != NavSettings) SaveSettingsFromUi();
            foreach (var (_, otherPage, _, _) in NavPages()) otherPage.IsVisible = false;
            page.IsVisible = true;
            PageTitle.Text = title;
            PageSubtitle.Text = subtitle;
            if (nav == NavIndex) ShowIndexPage();
            else if (nav == NavLua && luaRows.Count == 0 && !luaLoading) RefreshLuas();
            else if (nav == NavLibrary) RefreshLibrary();
            else if (nav == NavSettings) FillSettingsFromConfig();
            else if (nav == NavLogs) ApplyLogFilter();
            return;
        }
    }

    private string DumpDir() => AppPaths.ResolveDumpDir(currentConfig.DumpDirectory);

    private void AppLog(string message, string level = "INFO") => logQueue.Enqueue((level, message));

    private void FlushLog()
    {
        var appended = false;
        var sb = new StringBuilder();
        while (logQueue.TryDequeue(out var line))
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            sb.Append('[').Append(time).Append("] [").Append(line.Level).Append("] ").Append(line.Message).Append('\n');
            logLines.Add(new LogLine
            {
                Time = time, Level = line.Level, Message = line.Message,
                Severity = line.Level switch { "DEBUG" => 0, "INFO" => 1, "WARNING" => 2, _ => 3 },
            });
            appended = true;
        }
        if (!appended) return;
        while (logLines.Count > 6000) logLines.RemoveAt(0);
        LogBox.Text += sb.ToString();
        LogScroll.ScrollToEnd();
        if (PageLogs.IsVisible) ApplyLogFilter();
    }

    private void ShowQr(byte[][] matrix)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Use the Steam Mobile App to sign in with this QR code:").AppendLine();
        const char dark = '█', light = ' ';
        var size = matrix.Length;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++) sb.Append(matrix[y][x] != 0 ? dark : light);
            sb.Append('\n');
        }
        QrText.Text = sb.ToString();
        QrText.IsVisible = true;
    }

    private Task<string?> PromptForAuthCodeAsync(AuthPromptKind kind, string? email, bool previousCodeWasIncorrect)
    {
        var tcs = new TaskCompletionSource<string?>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                Activate();
                var dialog = new AuthCodeWindow(kind, email, previousCodeWasIncorrect);
                var ok = await dialog.ShowDialog<bool>(this);
                tcs.TrySetResult(ok ? dialog.Code : null);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    private bool HasSavedSession(string? username) =>
        !string.IsNullOrWhiteSpace(username) && AccountSettingsStore.Instance.LoginTokens.ContainsKey(username.Trim());

    private void SaveConfigFromUi()
    {
        currentConfig.Username = UsernameBox.Text?.Trim();
        currentConfig.Password = PasswordBox.Text ?? "";
        currentConfig.RememberPassword = RememberBox.IsChecked == true;
        currentConfig.UseQrCode = QrBox.IsChecked == true;
        try { currentConfig.Save(); } catch (Exception ex) { AppLog($"Could not save settings: {ex.Message}", "WARNING"); }
    }

    private void SetRunState(bool running)
    {
        isRunning = running;
        if (!running) { shuttingDown = false; PauseControl.Resume(); }
        StartButton.IsVisible = !running;
        StopButton.IsVisible = running;
        var (text, color) = !running ? ("Idle", Colors.Gray) : shuttingDown ? ("Shutting down", Colors.Orange) : ("Running", Colors.CornflowerBlue);
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
    }

    private async void StartButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (isRunning) return;
        SaveConfigFromUi();
        if (PageSettings.IsVisible) SaveSettingsFromUi();

        var hasSession = currentConfig.RememberPassword && HasSavedSession(currentConfig.Username);
        if (!hasSession && !currentConfig.UseQrCode && (string.IsNullOrWhiteSpace(currentConfig.Username) || string.IsNullOrEmpty(currentConfig.Password)))
        {
            AppLog("Enter your Steam username and password (or turn on QR code sign-in) first.", "WARNING");
            return;
        }

        var resume = false;
        if (currentConfig.AppIdsToProcess.Count == 0 &&
            RunCheckpoint.FindResumable(DumpDir(), currentConfig.Username ?? "", RunCheckpoint.ScopeKey(currentConfig.BranchFilter, currentConfig.DownloadManifests)) is { } pending)
        {
            resume = true;
            AppLog($"Resuming the interrupted dump: {pending.Done:N0} of {pending.Planned:N0} apps already finished are skipped.");
        }

        var args = new List<string>();
        if (currentConfig.UseQrCode && !hasSession) args.Add("-qr");
        if (!string.IsNullOrWhiteSpace(currentConfig.Username)) { args.Add("-username"); args.Add(currentConfig.Username); }
        if (!hasSession && !string.IsNullOrEmpty(currentConfig.Password)) { args.Add("-password"); args.Add(currentConfig.Password); }
        if (hasSession) AppLog($"Signing in as {currentConfig.Username} with the saved login.");
        if (resume) args.Add("-resume");

        tokensBeforeRun = new HashSet<string>(AccountSettingsStore.Instance.LoginTokens.Keys);
        shuttingDown = false;
        SetRunState(true);
        ProgressText.Text = "Connecting to Steam…";
        AppLog("Starting dump…");

        try
        {
            var code = await Task.Run(() => Program.MainAsync(args.ToArray()));
            AppLog(DepotDumper.StopRequested ? "Dump stopped." : code == 0 ? "Dump finished." : $"Dump ended with errors (exit code {code}). See the log.",
                   code == 0 ? "INFO" : "WARNING");
            ProgressText.Text = DepotDumper.StopRequested ? "Stopped. Anything already dumped was kept." : code == 0 ? "Finished." : "Finished with errors.";
        }
        catch (Exception ex)
        {
            AppLog($"Dump failed: {ex.Message}", "ERROR");
            ProgressText.Text = "Failed - see the log above.";
        }
        finally
        {
            SetRunState(false);
            AfterRunSessionCheck();
        }
    }

    private void AfterRunSessionCheck()
    {
        try
        {
            var added = AccountSettingsStore.Instance.LoginTokens.Keys.Where(k => !tokensBeforeRun.Contains(k)).ToList();
            if (added.Count == 1 && string.IsNullOrWhiteSpace(UsernameBox.Text)) UsernameBox.Text = added[0];
            if (RememberBox.IsChecked == true && HasSavedSession(UsernameBox.Text)) PasswordBox.Text = "";
            SaveConfigFromUi();
        }
        catch { /* best effort */ }
    }

    private void StopButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!isRunning || shuttingDown) return;
        shuttingDown = true;
        DepotDumper.RequestStop();
        SetRunState(true);
        ProgressText.Text = "Finishing the downloads already in flight - this takes a moment.";
        AppLog("Stop requested: finishing in-flight downloads, then stopping.", "WARNING");
    }

    private void UpdateProgress()
    {
        if (!isRunning) return;
        var s = StatisticsTracker.GetLive();
        var sp = Throttle.Snapshot();
        if (s.PlannedApps > 0)
        {
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = 100.0 * s.AppsDone / s.PlannedApps;
            if (!shuttingDown)
                ProgressText.Text = $"{s.AppsDone:N0} of {s.PlannedApps:N0} apps - {s.CurrentApp}  ·  {s.Manifests:N0} manifests ({s.ManifestsNew:N0} new)  ·  {s.Errors} errors" +
                                     (sp.Dynamic ? $"  ·  {sp.InFlight} depots working, limit {sp.Workers} of {sp.MaxWorkers}" : "");
        }
        else RunProgress.IsIndeterminate = true;
    }

    // ============================================================ game index

    private bool indexBusy, indexPresetsAdded;

    private void ShowIndexPage()
    {
        if (!indexPresetsAdded)
        {
            indexPresetsAdded = true;
            foreach (var preset in GameIndex.Presets)
            {
                var button = new Button { Content = preset.Name, Tag = preset, [ToolTip.TipProperty] = preset.Description, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 7, 12, 7) };
                button.Click += IndexPreset_Click;
                IndexPresetPanel.Children.Add(button);
            }
        }
        RefreshIndexStats();
    }

    private async void RefreshIndexStats()
    {
        var dir = DumpDir();
        try
        {
            if (!GameIndex.Exists(dir)) { IndexStatsText.Text = "Not built yet. Press Rebuild index."; IndexRebuildButton.Content = "Build index"; return; }
            var s = await Task.Run(() => GameIndex.Stats(dir));
            IndexRebuildButton.Content = "Rebuild index";
            IndexStatsText.Text = $"{s.Games:N0} games  ·  {s.Dlc:N0} DLC  ·  {s.Depots:N0} depots ({s.KeyedDepots:N0} with a key)  ·  {s.Manifests:N0} manifests ({s.ManifestsDownloaded:N0} downloaded)  ·  {s.Luas:N0} luas  ·  {s.Tokens:N0} tokens   (built {s.BuiltAt})";
        }
        catch (Exception ex) { IndexStatsText.Text = "The index could not be read: " + ex.Message; }
    }

    private async void IndexRebuild_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (indexBusy) return;
        indexBusy = true; IndexRebuildButton.IsEnabled = false; IndexStatsText.Text = "Building the index…";
        var dir = DumpDir();
        try { await Task.Run(() => GameIndex.Rebuild(dir, m => AppLog(m))); }
        catch (Exception ex) { AppLog($"Could not build the index: {ex.Message}", "ERROR"); }
        finally { indexBusy = false; IndexRebuildButton.IsEnabled = true; RefreshIndexStats(); }
    }

    private void IndexPreset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IndexPreset preset }) return;
        IndexSql.Text = preset.Sql;
        RunIndexQuery();
    }

    private void IndexRun_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RunIndexQuery();

    private async void RunIndexQuery()
    {
        var sql = (IndexSql.Text ?? "").Trim();
        var dir = DumpDir();
        if (sql.Length == 0) { IndexResultText.Text = "Pick a question above, or type a SELECT."; return; }
        if (!GameIndex.Exists(dir)) { IndexResultText.Text = "Build the index first."; return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => GameIndex.Query(dir, sql));

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columns = result.Columns.Select(name => { var u = name; var n = 2; while (!used.Add(u)) u = $"{name}_{n++}"; return u; }).ToList();

            IndexGrid.Columns.Clear();
            foreach (var name in columns)
                IndexGrid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding($"[{name}]") });

            var rows = result.Rows.Select(row =>
            {
                var dict = new Dictionary<string, object>();
                for (int i = 0; i < columns.Count; i++) dict[columns[i]] = row[i] is DBNull or null ? "" : row[i];
                return dict;
            }).ToList();
            IndexGrid.ItemsSource = rows;

            IndexResultText.Text = $"{result.Rows.Count:N0} row(s) in {clock.ElapsedMilliseconds} ms" + (result.Truncated ? "  ·  showing the first 5,000" : "");
        }
        catch (Exception ex)
        {
            IndexGrid.ItemsSource = null;
            IndexResultText.Text = "Query failed: " + ex.Message;
        }
    }

    // ============================================================ lua library

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
        var q = (LuaSearch.Text ?? "").Trim();
        IEnumerable<LuaEntry> rows = luaRows;
        if (q.Length > 0) rows = rows.Where(r => r.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = rows.OrderBy(r => r.App).ThenBy(r => r.FileName).ToList();
        LuaGrid.ItemsSource = list;
        LuaGrid.SelectedItem = null;
        LuaCount.Text = luaRows.Count == 0 ? "no luas yet" : $"{list.Count:N0} of {luaRows.Count:N0}";
    }

    private void LuaRefresh_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RefreshLuas();
    private void LuaSearch_Changed(object? sender, TextChangedEventArgs e) { if (LuaGrid != null) ApplyLuaFilter(); }

    private void LuaGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
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

    private async void LuaCopy_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = LuaGrid.SelectedItems.Cast<LuaEntry>().ToList();
        if (selected.Count == 0) { AppLog("Select one or more luas first.", "WARNING"); return; }
        try
        {
            var text = string.Join(Environment.NewLine + Environment.NewLine, selected.Select(s => File.ReadAllText(s.Path).TrimEnd()));
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) { AppLog("Could not access the clipboard.", "ERROR"); return; }
            await clipboard.SetTextAsync(text);
            AppLog(selected.Count == 1 ? $"Copied {selected[0].FileName}." : $"Copied {selected.Count} luas.");
        }
        catch (Exception ex) { AppLog($"Copy failed: {ex.Message}", "ERROR"); }
    }

    private async void LuaSave_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = LuaGrid.SelectedItems.Cast<LuaEntry>().ToList();
        if (selected.Count == 0) { AppLog("Select one or more luas first.", "WARNING"); return; }
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider == null) return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to save copies of the selected luas into" });
        if (folders.Count == 0) return;
        var targetDir = folders[0].TryGetLocalPath();
        if (targetDir == null) return;

        int saved = 0, skipped = 0;
        foreach (var s in selected)
        {
            try
            {
                var target = System.IO.Path.Combine(targetDir, s.FileName);
                if (File.Exists(target)) { skipped++; continue; }   // never overwrite something already there
                File.Copy(s.Path, target);
                saved++;
            }
            catch (Exception ex) { AppLog($"Could not copy {s.FileName}: {ex.Message}", "ERROR"); }
        }
        AppLog($"Saved {saved} lua(s) to {targetDir}" + (skipped > 0 ? $" ({skipped} already existed and were left alone)." : "."));
    }

    private void OpenLuaPool_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = Collector.LuaDir(DumpDir());
        try
        {
            Directory.CreateDirectory(path);
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch (Exception ex) { AppLog($"Could not open {path}: {ex.Message}", "ERROR"); }
    }

    // ============================================================ manifest library

    private sealed class LedgerRow
    {
        public string Status { get; init; } = "";
        public uint AppId { get; init; }
        public bool IsDownloaded { get; init; }
        public string App { get; init; } = "";
        public uint Depot { get; init; }
        public ulong Manifest { get; init; }
        public string Branches { get; init; } = "";
        public DateTime? Created { get; init; }
        public string CreatedText => Created?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        public string Source { get; init; } = "";
        public string SearchText { get; init; } = "";
    }

    private List<LedgerRow> ledgerRows = new();
    private bool libraryToolBusy;

    private void RefreshLibrary()
    {
        try
        {
            ManifestLedger.UseDirectory(DumpDir());
            var snapshot = ManifestLedger.Snapshot();
            ledgerRows = snapshot
                .OrderBy(x => x.AppId == 0)
                .ThenBy(x => x.AppId)
                .ThenByDescending(x => x.CreatedUtc ?? DateTime.MinValue)
                .ThenByDescending(x => x.FirstSeenUtc)
                .ThenByDescending(x => x.ManifestId)
                .Select(x =>
                {
                    var status = x.Downloaded ? "Downloaded" : x.Unavailable ? "Unavailable" : "Pending";
                    var branches = string.Join(", ", x.Branches);
                    return new LedgerRow
                    {
                        Status = status, AppId = x.AppId, IsDownloaded = x.Downloaded,
                        App = x.AppId == 0 ? "—" : x.AppId.ToString(), Depot = x.DepotId, Manifest = x.ManifestId,
                        Branches = branches, Created = x.CreatedUtc, Source = x.Source ?? "",
                        SearchText = $"{x.AppId} {x.DepotId} {x.ManifestId} {branches} {status}",
                    };
                }).ToList();
            ApplyLibraryFilter();
            UpdateLibraryStats();
        }
        catch (Exception ex) { AppLog($"Could not read the manifest library: {ex.Message}", "ERROR"); }
    }

    private void ApplyLibraryFilter()
    {
        var q = (LibrarySearch.Text ?? "").Trim();
        IEnumerable<LedgerRow> rows = ledgerRows;
        if (q.Length > 0) rows = rows.Where(r => r.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = rows.Take(3000).ToList();
        LibraryGrid.ItemsSource = list;
        LibraryGrid.SelectedItem = null;
        LibraryCount.Text = list.Count < ledgerRows.Count ? $"showing first {list.Count:N0}" : $"{list.Count:N0} shown";
    }

    private void UpdateLibraryStats()
    {
        var s = ManifestLedger.GetStats();
        LibKnown.Text = s.Total.ToString("N0");
        LibDownloaded.Text = s.Downloaded.ToString("N0");
        LibPending.Text = s.Pending.ToString("N0");
        LibUnavailable.Text = s.Unavailable.ToString("N0");
    }

    private void LibrarySearch_Changed(object? sender, TextChangedEventArgs e) { if (LibraryGrid != null) ApplyLibraryFilter(); }

    private const string DowngradeDefaultHint = "Select one or more downloaded manifests below (Ctrl / Shift to pick several), then copy the commands to install that version.";

    private void LibraryGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var n = LibraryGrid.SelectedItems.Count;
        DowngradeHint.Text = n == 0 ? DowngradeDefaultHint : $"{n:N0} manifest{(n == 1 ? "" : "s")} selected.";
    }

    private List<DowngradeHelper.Item>? GetDowngradeSelection()
    {
        var rows = LibraryGrid.SelectedItems.Cast<LedgerRow>().ToList();
        if (rows.Count == 0) { AppLog("Select one or more manifests in the table first (Ctrl / Shift to pick several).", "WARNING"); return null; }
        var usable = rows.Where(r => r.IsDownloaded && r.AppId != 0).ToList();
        if (usable.Count < rows.Count)
            AppLog($"{rows.Count - usable.Count} selected row(s) skipped: only downloaded manifests with a known app ID can be used.", "WARNING");
        if (usable.Count == 0) { AppLog("None of the selected rows can be used: they need to be downloaded and have an app ID.", "WARNING"); return null; }
        return usable.Select(r => new DowngradeHelper.Item(r.AppId, r.Depot, r.Manifest)).ToList();
    }

    private async void CopySteamConsole_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (GetDowngradeSelection() is not { } items) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) { AppLog("Could not access the clipboard.", "ERROR"); return; }
        await clipboard.SetTextAsync(DowngradeHelper.SteamConsole(items));
        AppLog($"Copied {items.Count} Steam console command(s). Open steam://open/console in your browser or Run box, then paste.");
    }

    private async void CopyDepotDownloader_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (GetDowngradeSelection() is not { } items) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) { AppLog("Could not access the clipboard.", "ERROR"); return; }
        await clipboard.SetTextAsync(DowngradeHelper.DepotDownloader(items, UsernameBox.Text));
        AppLog($"Copied {items.Count} DepotDownloader command(s).");
    }

    private void HistoryToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        currentConfig.DownloadHistoricalManifests = HistoryToggle.IsChecked == true;
        try { currentConfig.Save(); } catch (Exception ex) { AppLog($"Could not save settings: {ex.Message}", "WARNING"); }
    }

    private async Task RunLibraryToolAsync(string label, Func<string> work)
    {
        if (libraryToolBusy) return;
        libraryToolBusy = true;
        ScanLocalButton.IsEnabled = false; ImportIdsButton.IsEnabled = false; RetryUnavailableButton.IsEnabled = false;
        AppLog(label);
        try
        {
            var result = await Task.Run(work);
            AppLog(result);
        }
        catch (Exception ex) { AppLog($"{label.TrimEnd('.', '…')} failed: {ex.Message}", "ERROR"); }
        finally
        {
            libraryToolBusy = false;
            ScanLocalButton.IsEnabled = true; ImportIdsButton.IsEnabled = true; RetryUnavailableButton.IsEnabled = true;
            RefreshLibrary();
        }
    }

    private async void ScanLocal_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dir = DumpDir();
        await RunLibraryToolAsync("Scanning local files for manifests…", () =>
        {
            var o = Program.BuildCollectOptions(dir, manifestsOnly: true);
            o.PublicOnly = false; o.NoBeta = false; o.LatestOnly = false; o.IncludeSteamDepotCache = true;
            return Collector.Run(o, m => Logger.Info(m)).ToString();
        });
    }

    private async void ImportIds_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider == null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import manifest IDs",
            FileTypeFilter = new[] { new FilePickerFileType("Text or CSV") { Patterns = new[] { "*.txt", "*.csv" } }, FilePickerFileTypes.All },
        });
        if (files.Count == 0) return;
        var file = files[0].TryGetLocalPath();
        if (file == null) return;
        await RunLibraryToolAsync("Importing manifest IDs…", () =>
        {
            ManifestLedger.UseDirectory(DumpDir());
            var r = ManifestHistory.ImportIds(file);
            return $"Imported {r.Added:N0} new manifest IDs ({r.AlreadyKnown:N0} already known, {r.Invalid:N0} lines skipped).";
        });
    }

    private async void RetryUnavailable_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RunLibraryToolAsync("Resetting unavailable manifests…", () =>
        {
            ManifestLedger.UseDirectory(DumpDir());
            var n = ManifestLedger.ResetUnavailable();
            return $"{n:N0} manifests will be retried on the next run.";
        });
    }

    // ============================================================ settings

    private bool settingsLoading;

    private static HashSet<uint> ParseIds(string text)
    {
        var set = new HashSet<uint>();
        foreach (var part in text.Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(part.Trim(), out var id)) set.Add(id);
        return set;
    }

    private void FillSettingsFromConfig()
    {
        settingsLoading = true;
        var c = currentConfig;
        AppIdsBox.Text = c.AppIdsToProcess is { Count: > 0 } ? string.Join(", ", c.AppIdsToProcess) : "";
        ExcludedBox.Text = c.ExcludedAppIds is { Count: > 0 } ? string.Join(", ", c.ExcludedAppIds) : "";
        (string.Equals(c.BranchFilter, "public", StringComparison.OrdinalIgnoreCase) ? BranchPublic : BranchAll).IsChecked = true;

        DownloadManifestsToggle.IsChecked = c.DownloadManifests;
        KeepOldToggle.IsChecked = !c.DeleteOldManifests;

        CollectAfterToggle.IsChecked = c.CollectAfterRun;
        CollectPublicToggle.IsChecked = c.CollectPublicOnly;
        CollectNoBetaToggle.IsChecked = c.CollectNoBeta;
        CollectLatestToggle.IsChecked = c.CollectLatestOnly;
        CollectDepotCacheToggle.IsChecked = c.CollectIncludeSteamDepotCache;

        DynamicToggle.IsChecked = c.DynamicConcurrency;
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

        UpdateDumpDirResolved();
        settingsLoading = false;
    }

    private void SaveSettingsFromUi()
    {
        var c = currentConfig;
        c.AppIdsToProcess = ParseIds(AppIdsBox.Text ?? "");
        c.ExcludedAppIds = ParseIds(ExcludedBox.Text ?? "");
        c.BranchFilter = BranchPublic.IsChecked == true ? "public" : null;

        c.DownloadManifests = DownloadManifestsToggle.IsChecked == true;
        c.DeleteOldManifests = KeepOldToggle.IsChecked != true;

        c.CollectAfterRun = CollectAfterToggle.IsChecked == true;
        c.CollectPublicOnly = CollectPublicToggle.IsChecked == true;
        c.CollectNoBeta = CollectNoBetaToggle.IsChecked == true;
        c.CollectLatestOnly = CollectLatestToggle.IsChecked == true;
        c.CollectIncludeSteamDepotCache = CollectDepotCacheToggle.IsChecked == true;

        c.DynamicConcurrency = DynamicToggle.IsChecked == true;
        if (int.TryParse(MaxDownloadsBox.Text, out var md) && md > 0) c.MaxDownloads = md;
        if (int.TryParse(MaxParallelDepotsBox.Text, out var mpd) && mpd > 0) c.MaxParallelDepots = Math.Min(mpd, 64);
        c.MaxMemoryGb = double.TryParse((MaxMemoryBox.Text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0 ? gb : 0;
        if (int.TryParse(MaxConcurrentAppsBox.Text, out var ma) && ma > 0) c.MaxConcurrentApps = ma;
        if (int.TryParse(MaxServersBox.Text, out var ms) && ms > 0) c.MaxServers = ms;
        if (int.TryParse(CellIdBox.Text, out var cell) && cell >= 0) c.CellID = cell;
        c.LoginID = uint.TryParse(LoginIdBox.Text, out var lid) ? lid : null;

        c.DumpDirectory = string.IsNullOrWhiteSpace(DumpDirBox.Text) ? "dumps" : DumpDirBox.Text.Trim();
        c.LogLevel = LogLevelDebug.IsChecked == true ? "Debug"
                   : LogLevelWarning.IsChecked == true ? "Warning"
                   : LogLevelError.IsChecked == true ? "Error" : "Info";

        try { c.Save(); } catch (Exception ex) { AppLog($"Could not save settings: {ex.Message}", "WARNING"); }
    }

    private void UpdateDumpDirResolved()
    {
        if (DumpDirResolved == null) return;
        DumpDirResolved.Text = "Full path: " + AppPaths.ResolveDumpDir(DumpDirBox.Text);
    }

    private void DumpDirBox_TextChanged(object? sender, TextChangedEventArgs e) { if (!settingsLoading) UpdateDumpDirResolved(); }

    private async void Browse_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider == null) return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select dump directory" });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (path != null) DumpDirBox.Text = path;
    }

    // ============================================================ logs

    private sealed class LogLine
    {
        public string Time { get; init; } = "";
        public string Level { get; init; } = "INFO";
        public string Message { get; init; } = "";
        public int Severity { get; init; }
    }

    private readonly List<LogLine> logLines = new();

    private int MinLogSeverity => FilterError.IsChecked == true ? 3 : FilterWarn.IsChecked == true ? 2 : FilterInfo.IsChecked == true ? 1 : 0;

    private void ApplyLogFilter()
    {
        var min = MinLogSeverity;
        var q = (LogSearch.Text ?? "").Trim();
        IEnumerable<LogLine> rows = logLines.Where(l => l.Severity >= min);
        if (q.Length > 0) rows = rows.Where(l => l.Message.Contains(q, StringComparison.OrdinalIgnoreCase));
        LogList.ItemsSource = rows.ToList();
        if (AutoScrollToggle.IsChecked == true) LogScrollFull.ScrollToEnd();
    }

    private void LogFilter_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (PageLogs.IsVisible) ApplyLogFilter(); }
    private void LogSearch_Changed(object? sender, TextChangedEventArgs e) { if (PageLogs.IsVisible) ApplyLogFilter(); }

    private void ClearLog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        logLines.Clear();
        LogList.ItemsSource = null;
    }

    private void OpenLogsFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(DumpDir(), "logs");
        try
        {
            Directory.CreateDirectory(path);
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch (Exception ex) { AppLog($"Could not open {path}: {ex.Message}", "ERROR"); }
    }
}
