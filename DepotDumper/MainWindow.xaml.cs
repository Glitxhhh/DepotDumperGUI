using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;
using System.Collections.Generic;

namespace DepotDumper.GUI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ConfigFile currentConfig;
    private CancellationTokenSource? cancellationTokenSource;
    private bool isRunning = false;

    public MainWindow()
    {
        InitializeComponent();
        currentConfig = new ConfigFile();
        LoadDefaultConfig();

        // Set the UI dispatcher for Steam3Session events
        Steam3Session.UIDispatcher = this.Dispatcher;

        // Subscribe to logger events
        Logger.OnLogMessage += Logger_OnLogMessage;

        // Subscribe to QR code events
        Steam3Session.OnQrCodeGenerated += Steam3Session_OnQrCodeGenerated;
    }

    private void Steam3Session_OnQrCodeGenerated(string challengeUrl, byte[][] qrMatrix)
    {
        try
        {
            var qrWindow = new QrCodeWindow(challengeUrl, qrMatrix);
            qrWindow.Owner = this;
            qrWindow.Show(); // Non-modal so authentication can continue
        }
        catch (Exception ex)
        {
            LogMessage($"Error showing QR window: {ex.Message}");
            MessageBox.Show($"Error showing QR code: {ex.Message}\n\nStack: {ex.StackTrace}",
                "QR Code Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadDefaultConfig()
    {
        try
        {
            // Try to load existing config
            currentConfig = ConfigFile.Load();
            UpdateUIFromConfig();
        }
        catch (Exception ex)
        {
            LogMessage($"Could not load config: {ex.Message}");
            currentConfig = new ConfigFile();
        }
    }

    private void UpdateUIFromConfig()
    {
        Dispatcher.Invoke(() =>
        {
            UsernameTextBox.Text = currentConfig.Username ?? "";
            RememberPasswordCheckBox.IsChecked = currentConfig.RememberPassword;
            UseQrCodeCheckBox.IsChecked = currentConfig.UseQrCode;
            DumpDirectoryTextBox.Text = currentConfig.DumpDirectory ?? "dumps";
            MaxDownloadsTextBox.Text = currentConfig.MaxDownloads.ToString();
            MaxServersTextBox.Text = currentConfig.MaxServers.ToString();
            CellIDTextBox.Text = currentConfig.CellID.ToString();
            MaxConcurrentAppsTextBox.Text = currentConfig.MaxConcurrentApps.ToString();
            DownloadManifestsCheckBox.IsChecked = currentConfig.DownloadManifests;

            if (!string.IsNullOrEmpty(currentConfig.LoginID?.ToString()))
            {
                LoginIDTextBox.Text = currentConfig.LoginID.ToString();
            }

            // Set log level
            LogLevelComboBox.SelectedItem = LogLevelComboBox.Items
                .Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Content.ToString() == currentConfig.LogLevel)
                ?? LogLevelComboBox.Items[1];

            // Load app IDs
            if (currentConfig.AppIdsToProcess != null && currentConfig.AppIdsToProcess.Count > 0)
            {
                AppIdTextBox.Text = string.Join(", ", currentConfig.AppIdsToProcess);
            }

            // Load excluded app IDs
            if (currentConfig.ExcludedAppIds != null && currentConfig.ExcludedAppIds.Count > 0)
            {
                ExcludedAppIdsTextBox.Text = string.Join(", ", currentConfig.ExcludedAppIds);
            }
        });
    }

    private void UpdateConfigFromUI()
    {
        currentConfig.Username = UsernameTextBox.Text;
        currentConfig.Password = PasswordBox.Password;
        currentConfig.RememberPassword = RememberPasswordCheckBox.IsChecked ?? false;
        currentConfig.UseQrCode = UseQrCodeCheckBox.IsChecked ?? false;
        currentConfig.DumpDirectory = DumpDirectoryTextBox.Text;
        currentConfig.DownloadManifests = DownloadManifestsCheckBox.IsChecked ?? true;

        // Handle public-only filter by adding -branch argument
        bool publicOnly = PublicOnlyCheckBox.IsChecked ?? false;

        if (int.TryParse(MaxDownloadsTextBox.Text, out int maxDownloads))
            currentConfig.MaxDownloads = maxDownloads;

        if (int.TryParse(MaxServersTextBox.Text, out int maxServers))
            currentConfig.MaxServers = maxServers;

        if (int.TryParse(CellIDTextBox.Text, out int cellID))
            currentConfig.CellID = cellID;

        if (int.TryParse(MaxConcurrentAppsTextBox.Text, out int maxConcurrentApps))
            currentConfig.MaxConcurrentApps = maxConcurrentApps;

        if (uint.TryParse(LoginIDTextBox.Text, out uint loginID))
            currentConfig.LoginID = loginID;
        else
            currentConfig.LoginID = null;

        if (LogLevelComboBox.SelectedItem is ComboBoxItem selectedItem)
            currentConfig.LogLevel = selectedItem.Content.ToString() ?? "Info";

        // Parse app IDs
        currentConfig.AppIdsToProcess.Clear();
        if (!string.IsNullOrWhiteSpace(AppIdTextBox.Text))
        {
            var appIds = AppIdTextBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var appIdStr in appIds)
            {
                if (uint.TryParse(appIdStr.Trim(), out uint appId))
                {
                    currentConfig.AppIdsToProcess.Add(appId);
                }
            }
        }

        // Parse excluded app IDs
        currentConfig.ExcludedAppIds.Clear();
        if (!string.IsNullOrWhiteSpace(ExcludedAppIdsTextBox.Text))
        {
            var excludedIds = ExcludedAppIdsTextBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var appIdStr in excludedIds)
            {
                if (uint.TryParse(appIdStr.Trim(), out uint appId))
                {
                    currentConfig.ExcludedAppIds.Add(appId);
                }
            }
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Dump Directory",
            InitialDirectory = Directory.Exists(DumpDirectoryTextBox.Text)
                ? Path.GetFullPath(DumpDirectoryTextBox.Text)
                : Environment.CurrentDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            DumpDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Configuration",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                currentConfig = ConfigFile.Load(dialog.FileName);
                UpdateUIFromConfig();
                LogMessage($"Configuration loaded from: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading configuration: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Configuration",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "config.json",
            InitialDirectory = AppContext.BaseDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                UpdateConfigFromUI();
                currentConfig.Save(dialog.FileName);
                LogMessage($"Configuration saved to: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving configuration: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (isRunning)
        {
            MessageBox.Show("A dump operation is already running!", "Warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Update config from UI
        UpdateConfigFromUI();

        // Validate settings
        if (string.IsNullOrWhiteSpace(currentConfig.Username) && !currentConfig.UseQrCode)
        {
            MessageBox.Show("Please enter a username or enable QR Code login.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(currentConfig.Password) && !currentConfig.UseQrCode)
        {
            MessageBox.Show("Please enter a password or enable QR Code login.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        isRunning = true;
        cancellationTokenSource = new CancellationTokenSource();

        // Update UI state
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        UpdateStatus("Running...");
        ProgressBar.IsIndeterminate = true;

        try
        {
            LogMessage("DEBUG: About to call RunDepotDumper");
            await Task.Run(async () => await RunDepotDumper(), cancellationTokenSource.Token);
            LogMessage("DEBUG: RunDepotDumper completed");

            if (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                LogMessage("Dump completed successfully!");
                UpdateStatus("Completed");
            }
            else
            {
                LogMessage("Dump operation was cancelled.");
                UpdateStatus("Cancelled");
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage("Dump operation was cancelled by user.");
            UpdateStatus("Cancelled");
        }
        catch (Exception ex)
        {
            LogMessage($"Error during dump: {ex.Message}");
            LogMessage($"ERROR STACK TRACE: {ex.StackTrace}");
            UpdateStatus("Error");
            MessageBox.Show($"An error occurred: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            isRunning = false;
            Dispatcher.Invoke(() =>
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                ProgressBar.IsIndeterminate = false;
            });
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested)
        {
            LogMessage("Cancelling operation...");
            UpdateStatus("Cancelling...");

            // Cancel the token - this will signal the operation to stop
            try
            {
                cancellationTokenSource.Cancel();
                LogMessage("Cancellation requested. The operation will stop gracefully.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error cancelling token: {ex.Message}");
            }

            // The Steam session will be cleaned up naturally by the cancellation
            // Don't manually abort it as it causes threading issues
        }
    }

    private async Task RunDepotDumper()
    {
        try
        {
            LogMessage("DEBUG: RunDepotDumper - Start");

            // Apply configuration
            currentConfig.ApplyToDepotDumperConfig();
            LogMessage("DEBUG: Config applied");

            // Initialize logger with selected log level
            Logger.SetLogLevel(currentConfig.LogLevel);
            LogMessage("DEBUG: Logger initialized");

            LogMessage("Initializing Steam connection...");

            // Build arguments for DepotDumper
            var args = new List<string>();

            if (!string.IsNullOrWhiteSpace(currentConfig.Username))
            {
                args.Add("-username");
                args.Add(currentConfig.Username);
            }

            if (!string.IsNullOrWhiteSpace(currentConfig.Password))
            {
                args.Add("-password");
                args.Add(currentConfig.Password);
            }

            if (currentConfig.UseQrCode)
            {
                args.Add("-qr");
            }

            // Add app IDs if specified
            if (currentConfig.AppIdsToProcess != null && currentConfig.AppIdsToProcess.Count > 0)
            {
                foreach (var appId in currentConfig.AppIdsToProcess)
                {
                    args.Add("-appid");
                    args.Add(appId.ToString());
                }
            }

            LogMessage("DEBUG: About to check PublicOnlyCheckBox");

            // Add public-only filter if checked - MUST USE DISPATCHER
            bool publicOnly = false;
            Dispatcher.Invoke(() =>
            {
                publicOnly = PublicOnlyCheckBox.IsChecked == true;
            });

            if (publicOnly)
            {
                args.Add("-branch");
                args.Add("public");
            }

            // Add manifest download flag
            if (!currentConfig.DownloadManifests)
            {
                args.Add("-no-manifests");
            }

            LogMessage($"DEBUG: DownloadManifests = {currentConfig.DownloadManifests}");
            LogMessage($"DEBUG: PublicOnly = {publicOnly}");
            LogMessage($"DEBUG: Args = {string.Join(" ", args)}");
            LogMessage("DEBUG: About to call Program.MainAsync");
            // Run the actual dumper
            await Program.MainAsync(args.ToArray());
            LogMessage("DEBUG: Program.MainAsync completed");
        }
        catch (Exception ex)
        {
            LogMessage($"Fatal error: {ex.Message}");
            LogMessage($"Fatal error stack: {ex.StackTrace}");
            throw;
        }
    }

    private void Logger_OnLogMessage(string level, string message)
    {
        LogMessage($"[{level}] {message}");
    }

    private void LogMessage(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            LogTextBox.ScrollToEnd();
        });
    }

    private void UpdateStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            StatusTextBlock.Text = status;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        Logger.OnLogMessage -= Logger_OnLogMessage;
        Steam3Session.OnQrCodeGenerated -= Steam3Session_OnQrCodeGenerated;
        base.OnClosed(e);
    }
}
