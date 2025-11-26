# DepotDumper Usage Guide

## Quick Start

### GUI Mode (Recommended for Desktop Use)
Simply **double-click** `DepotDumper.exe` from Windows Explorer, or run:
```cmd
DepotDumper.exe
```

The graphical interface will launch automatically.

### CLI Mode (For Command Line / Automation)
Run with **any argument** to use command-line mode:

```cmd
# Show version
DepotDumper.exe -V

# Dump specific app
DepotDumper.exe -appid 730

# Use username/password
DepotDumper.exe -username myuser -password mypass -appid 440

# Lua-only mode (no manifests)
DepotDumper.exe -appid 730 -no-manifests
```

## Mode Detection

The application automatically detects which mode to use:

| Scenario | Mode |
|----------|------|
| Double-click from Windows Explorer | **GUI** |
| Run without arguments (`DepotDumper.exe`) | **GUI** |
| Run with any argument (`DepotDumper.exe -appid 730`) | **CLI** |
| Explicit GUI mode (`DepotDumper.exe -gui`) | **GUI** |
| Explicit CLI mode (`DepotDumper.exe -cli`) | **CLI** |

## Console Attachment

The application is compiled as **WinExe** which means:
- ✅ **No console window pops up** when running GUI mode
- ✅ **Console automatically attaches** when running CLI mode
- ✅ **Works from PowerShell, CMD, or Bash**

## Features by Mode

### GUI Mode Features
- ✨ Dark-themed visual interface
- 🔐 QR Code popup for Steam Mobile login
- 📊 Real-time log display
- ⚙️ All settings in visual controls
- 💾 Load/Save configuration files
- 📈 Progress bar and status indicator

### CLI Mode Features
- 🖥️ Full command-line interface
- 🔧 All original parameters supported
- 🤖 Perfect for automation/scripting
- 📝 Console logging with colors
- 🔐 QR Code display in ASCII (console)

## Common Commands

### Download Specific App
```cmd
DepotDumper.exe -appid 730
```

### Download Multiple Apps
```cmd
DepotDumper.exe -appid 730 -appid 440 -appid 570
```

### QR Code Login
```cmd
DepotDumper.exe -qr -appid 730
```

### Lua-Only Dumps (No Manifests)
```cmd
DepotDumper.exe -appid 730 -no-manifests
```

### Custom Output Directory
```cmd
DepotDumper.exe -appid 730 -dump-directory "C:\MyDumps"
```

### With Configuration File
```cmd
DepotDumper.exe -config "myconfig.json"
```

## Configuration File

Create `config.json` in the same directory as the exe:

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
  "AppIdsToProcess": [],
  "ExcludedAppIds": []
}
```

Then just run:
```cmd
DepotDumper.exe
```

The GUI will load the configuration automatically, or use it in CLI:
```cmd
DepotDumper.exe -appid 730
```

## Troubleshooting

### "Application won't open"
**Solution**: Make sure you have .NET 9.0 Runtime installed
- Download from: https://dotnet.microsoft.com/download/dotnet/9.0

### "Console doesn't appear in CLI mode"
**Solution**: This is expected! The console attaches automatically when you pass arguments.
- Run from CMD/PowerShell/Terminal with arguments
- Example: `DepotDumper.exe -V`

### "GUI doesn't appear when double-clicking"
**Solution**:
1. Check Task Manager - the process might be running
2. Try running with `-gui` flag explicitly
3. Check for error dialogs

### "QR Code doesn't show"
**GUI Mode**: Should appear in a popup window - check for blocked windows
**CLI Mode**: Displays as ASCII art in the console

## Build from Source

```cmd
cd DepotDumperMorrenusEdition2\DepotDumper
dotnet build --configuration Release
```

Output: `bin\Release\net9.0-windows\DepotDumper.exe`

## All Command-Line Arguments

```
Login:
  -username <user>       Steam username
  -password <pass>       Steam password
  -qr                    Use QR Code login
  -remember-password     Save login token
  -cellid <id>           Steam download region

App Selection:
  -appid <id>            Specific app ID to dump
  -appids-file <path>    File with list of app IDs
  -exclude-app <id>      Exclude specific app IDs
  -include-app <id>      Include specific app IDs

Download Options:
  -download-manifests <true|false>  Download .manifest files
  -no-manifests                     Skip manifest downloads (Lua only)
  -dump-directory <path>            Output directory
  -max-downloads <n>                Concurrent downloads
  -max-servers <n>                  CDN server pool size
  -max-concurrent-apps <n>          Process N apps concurrently

Logging:
  -log-level <level>     Debug|Info|Warning|Error|Critical

Mode Control:
  -gui                   Force GUI mode
  -cli                   Force CLI mode
  -V, --version          Show version info
```

## Examples

### Example 1: Download Counter-Strike 2
```cmd
DepotDumper.exe -appid 730 -username myuser -password mypass
```

### Example 2: Download with QR Login (GUI)
1. Run: `DepotDumper.exe`
2. Check "Use QR Code Login"
3. Enter App ID: 730
4. Click "Start Dump"
5. QR popup appears - scan with Steam Mobile

### Example 3: Automated Lua-Only Dump
```cmd
DepotDumper.exe -appid 730 -no-manifests -dump-directory "C:\Dumps"
```

### Example 4: Process Multiple Apps
```cmd
DepotDumper.exe -appid 730 -appid 440 -appid 570 -max-concurrent-apps 2
```

### Example 5: Debug Mode
```cmd
DepotDumper.exe -appid 730 -log-level Debug
```

## Output Structure

### With Manifests (Default)
```
dumps/
└── 730/
    └── 730.public.2025-11-08_12-00-00.Counter-Strike_2/
        └── 730.zip
            ├── 731_1234567890.manifest
            ├── 732_1234567891.manifest
            └── 730.lua
```

### Lua-Only Mode
```
dumps/
└── 730/
    └── 730.public.2025-11-08_12-00-00.Counter-Strike_2/
        └── 730.zip
            └── 730.lua  (only Lua file)
```

## Support

For issues or questions, please check:
- README_COMBINED.md for detailed documentation
- GitHub Issues for known problems
- .NET 9.0 installation for runtime requirements
