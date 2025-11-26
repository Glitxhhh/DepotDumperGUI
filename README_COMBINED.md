# DepotDumper - Morrenus Edition (Combined CLI + GUI)

A unified Steam Depot Manifest Downloader with both Command-Line Interface and Graphical User Interface in a single executable.

## Features

### ✨ Unified Executable
- **Single .exe file** - No more multiple executables!
- **Automatic mode detection** - Launches GUI by default, CLI when arguments provided
- **Manual override** - Use `-gui` or `-cli` flags to force a specific mode

### 🎨 Graphical User Interface
- Modern dark-themed Windows application
- **QR Code Popup** - Displays Steam Mobile QR codes in a dedicated window
- Real-time logging and progress tracking
- Full configuration management (Load/Save config.json)
- All settings accessible through visual controls

### 💻 Command-Line Interface
- Complete CLI functionality preserved
- All original command-line arguments supported
- Perfect for automation and scripting

### 🔧 New Manifest Toggle Feature
- **Download Manifests** setting (on/off)
- When enabled: Downloads both .manifest and .lua files
- When disabled: Downloads only .lua files in `/dumps/appid.zip`
- Controlled via GUI checkbox or CLI `-no-manifests` flag

## Usage

### GUI Mode (Default)

Simply double-click `DepotDumper.exe` or run without arguments:

```bash
DepotDumper.exe
# or explicitly
DepotDumper.exe -gui
```

The GUI will launch with:
- All settings in visual form
- QR Code display for Steam Mobile login
- Real-time output log
- Progress tracking
- Load/Save configuration buttons

### CLI Mode

Run with any command-line argument to use CLI mode:

```bash
# Dump specific app
DepotDumper.exe -appid 730

# Dump with username/password
DepotDumper.exe -username myuser -password mypass -appid 440

# Dump with QR Code (shows in console)
DepotDumper.exe -qr -appid 570

# Lua-only dump (no manifests)
DepotDumper.exe -appid 730 -no-manifests

# Force CLI mode even without args
DepotDumper.exe -cli
```

## QR Code Support

### GUI Mode
When QR Code login is enabled in GUI:
1. Check "Use QR Code Login" checkbox
2. Click "Start Dump"
3. **QR Code appears in a popup window**
4. Scan with Steam Mobile App
5. Window closes automatically after authentication

### CLI Mode
When using `-qr` flag in CLI:
- QR Code displays in console (ASCII art)
- Works exactly as before

## Command-Line Arguments

### Login Options
```
-username <user>    Steam username
-password <pass>    Steam password
-qr                 Use QR Code login (console or GUI popup)
-remember-password  Save login token
```

### App Selection
```
-appid <id>         Specific app ID to dump
-appids-file <path> File containing list of app IDs
-exclude-app <id>   Exclude specific app IDs
```

### Download Settings
```
-download-manifests <true|false>  Download manifest files
-no-manifests                     Shortcut for -download-manifests false
-dump-directory <path>            Output directory (default: dumps)
-max-downloads <n>                Concurrent downloads (default: 4)
-max-servers <n>                  CDN server pool size (default: 20)
```

### Advanced
```
-cellid <id>              Steam download region
-loginid <id>             For concurrent instances
-max-concurrent-apps <n>  Process multiple apps (default: 1)
-log-level <level>        Debug|Info|Warning|Error|Critical
```

### Mode Control
```
-gui    Force GUI mode
-cli    Force CLI mode
```

## Configuration File

Both GUI and CLI share the same `config.json` format:

```json
{
  "Username": "your_username",
  "Password": "your_password",
  "RememberPassword": true,
  "UseQrCode": false,
  "DumpDirectory": "dumps",
  "DownloadManifests": true,
  "MaxDownloads": 4,
  "MaxServers": 20,
  "CellID": 0,
  "MaxConcurrentApps": 1,
  "LogLevel": "Info",
  "AppIdsToProcess": [730, 440, 570],
  "ExcludedAppIds": [99999]
}
```

## Output Structure

### With Manifests (DownloadManifests = true)
```
dumps/
├── {AppID}/
│   └── {AppID}.{Branch}.{Date}.{AppName}/
│       └── {AppID}.zip
│           ├── {DepotID}_{ManifestID}.manifest
│           ├── {DepotID}_{ManifestID}.manifest
│           └── {AppID}.lua
```

### Lua-Only Mode (DownloadManifests = false)
```
dumps/
├── {AppID}/
│   └── {AppID}.{Branch}.{Date}.{AppName}/
│       └── {AppID}.zip
│           └── {AppID}.lua  (only Lua files)
```

## Building from Source

```bash
cd "DepotDumperMorrenusEdition2/DepotDumper"

# Restore dependencies
dotnet restore

# Build Release
dotnet build --configuration Release

# Output location
# bin/Release/net9.0-windows/DepotDumper.exe
```

## Requirements

- **.NET 9.0 Runtime** (or SDK for building)
- **Windows OS** (WPF is Windows-only)
- **Valid Steam Account**

## Technical Details

### Architecture
- **Single Entry Point**: `Program.Main()` detects mode automatically
- **GUI Namespace**: `DepotDumper.GUI` (MainWindow, QrCodeWindow, App)
- **Core Namespace**: `DepotDumper` (all CLI logic, Steam integration)
- **Event-Based Logging**: Logger fires events that GUI subscribes to
- **QR Code Events**: Steam3Session notifies GUI when QR codes are generated

### Mode Detection Logic
```csharp
bool isGuiMode = HasParameter(args, "-gui") ||
                 HasParameter(args, "--gui") ||
                 (args.Length == 0 && !Console.IsInputRedirected);

if (isGuiMode && !HasParameter(args, "-cli"))
    LaunchGUI();
else
    LaunchCLI();
```

## Differences from Original

### Added Features
1. **Graphical User Interface** - Full-featured Windows application
2. **QR Code Popup Window** - Visual QR code display in GUI
3. **Manifest Download Toggle** - Option to skip .manifest downloads
4. **Unified Executable** - Single file for both GUI and CLI
5. **Enhanced Logging** - Real-time log events for GUI

### Configuration Changes
- New setting: `DownloadManifests` (boolean)
- New CLI flags: `-gui`, `-cli`, `-no-manifests`, `-download-manifests`
- Compatible with all existing configs

## Tips

- **GUI by Default**: Just double-click the .exe
- **CLI for Scripts**: Add any argument to trigger CLI mode
- **Lua-Only Dumps**: Uncheck "Download Manifests" in GUI or use `-no-manifests`
- **QR Code Login**: Works in both modes with appropriate display
- **Config Files**: Created/loaded from application directory

## Troubleshooting

**Q: GUI won't start**
- Check .NET 9.0 is installed
- Try running with `-gui` flag explicitly

**Q: CLI mode starts instead of GUI**
- Remove all command-line arguments
- Or add `-gui` flag

**Q: QR Code doesn't show in GUI**
- Make sure "Use QR Code Login" is checked
- QR window should popup automatically after clicking "Start Dump"

**Q: Can't find the executable**
- Build location: `bin/Release/net9.0-windows/DepotDumper.exe`
- Or `bin/Debug/net9.0-windows/DepotDumper.exe`

## Credits

- **Original DepotDumper**: SteamRE Team
- **GUI & Enhancements**: Morrenus Edition
- **QR Code Support**: QRCoder library
- **Steam Integration**: SteamKit2

## License

Same as original DepotDumper
