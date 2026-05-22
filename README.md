# VX Connector

An unofficial Windows helper that lets your **ProTee VX** launch monitor talk to **Infinite Tees** or **Drills** through ProTee Labs's OpenConnect path. VX Connector runs in the system tray and has two modes:

- **Direct mode (default)** — VX Connector just runs as a process so ProTee Labs's `GSPconnect.exe` check passes. Your simulator (Infinite Tees or Drills) listens on port 921 directly, and ProTee Labs talks to it without anything in the data path.
- **Folder Watcher mode** — VX Connector watches ProTee Labs's shots folder, converts each new shot to OpenConnect JSON, and forwards it to whichever sim is configured. Useful as a backup when the direct path isn't an option.

```
Direct mode:
ProTee VX  →  ProTee Labs Software  →  Infinite Tees / Drills (:921)
                                  ↑
                                  │ checks for GSPconnect.exe to be running
                                  │
                              VX Connector (idle, just present)

Folder Watcher mode:
ProTee VX  →  ProTee Labs  →  ProTeeUnited\Shots\* (on disk)
                                          ↓ (FileSystemWatcher)
                                  VX Connector  →  Infinite Tees / Drills
```

## Quick Start

1. **Download** `GSPconnect.exe` from the [latest release](https://github.com/zonkey-acoustic/itees-vx-connector/releases/latest) (or build from source — see below).

2. **Configure your simulator to listen on port 921:**
   - **Infinite Tees** — right-click VX Connector's tray icon and pick **"Switch Infinite Tees to Direct mode (port 921)"**, then restart iTees. (You can also run `scripts/set-itees-port.ps1` manually.)
   - **Drills** — Drills's helper already listens on port 921 by default. Confirm in `C:\Program Files\DrillsGolfSimulator\Drills\DrillsConnect\settings.json` if you'd like (`openConnect.port` should be `921`).

3. **Launch VX Connector first** — double-click `GSPconnect.exe`. The tray icon appears blue with the detected sim in its tooltip (e.g. *"VX Proxy — Direct mode (Infinite Tees)"*).

4. **Launch your simulator** (iTees or Drills).

5. **Launch ProTee Labs**, go to **Game Options → Game: GSPro**, and hit **Connect**. ProTee Labs talks straight to your sim on port 921; VX Connector just keeps existing so the gate stays open.

> **Important:** VX Connector must be running *before* you click Connect in ProTee Labs. ProTee Labs's pre-flight check looks for a `GSPconnect.exe` process before opening a connection.

### Folder Watcher mode

If the Direct path isn't viable for your setup, VX Connector can pick up shots from disk instead:

- Right-click the tray icon → **"Switch to Folder Watcher mode"** (or launch with `GSPconnect.exe --folder-watcher`).
- The tray icon turns purple; the tooltip shows the resolved forward target (e.g. *"VX Proxy — Folder watcher → Drills :921"*).
- Each new shot directory under `%APPDATA%\ProTeeUnited\Shots` is parsed and forwarded to your sim's configured port.

You can switch back to Direct mode from the same menu without restarting.

### Upgrading from an earlier release

If you ran older versions of VX Connector that configured iTees on port 999, run `scripts/reset-itees-port.ps1` once before starting the new version, or use the tray menu's iTees switcher. iTees needs to be on **921** for Direct mode to work end-to-end.

## System Tray

| Color  | Meaning                                                              |
|--------|----------------------------------------------------------------------|
| Blue   | Direct mode — VX Connector is idle; ProTee Labs talks to the sim directly |
| Purple | Folder Watcher mode — VX Connector is forwarding shots from disk     |
| Red    | Stopped                                                              |

Hover the tray icon for the detected sim and forward target. Double-click to open the log window. Right-click for the mode switcher and other actions:

- **Show Log** — open the log window
- **Switch to Direct mode** / **Switch to Folder Watcher mode** — toggle modes at runtime
- **Switch Infinite Tees to Direct mode (port 921)** — only visible when iTees is currently on 999
- **Quit** — exit VX Connector

VX Connector also enforces single-instance: launching it again will kill any prior instance running from the same binary path.

## Build from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd dotnet
dotnet run                      # Run in development mode
dotnet publish -c Release       # Build standalone exe
```

The published exe is at `dotnet/bin/Release/net8.0-windows/win-x64/publish/GSPconnect.exe` — single file, no .NET runtime required on the target machine.

## How It Works

ProTee Labs's "Connect" action runs a pre-flight check that requires a process named `GSPconnect.exe` to be in the process list. Once that gate passes, ProTee Labs opens a TCP connection to its target simulator and sends shot data using the **OpenConnect API** — newline-terminated JSON over raw TCP. VX Connector's role is to satisfy the pre-flight check; the rest depends on the mode:

- **Direct mode**: VX Connector binds no port and forwards nothing. Your sim listens on port 921 and receives ProTee Labs's traffic directly.
- **Folder Watcher mode**: VX Connector watches the folder ProTee Labs writes each shot's `ShotData.json` into, converts that data to the OpenConnect format, and forwards it to whichever sim is configured. The destination is re-resolved per shot (so toggling iTees direct/proxy mid-session is fine).

VX Connector's executable is named and version-stamped to match `C:\GSPro\Core\GSPC\GSPconnect.exe` so ProTee Labs's process-name gate accepts it. It does not impersonate the licensed GSPro Connect for any deeper purpose; ProTee Labs routes to a separate, unauthenticated code path when our impersonator is present.

## Note

This unofficial integration is community-developed and provided as-is, without official support from the respective brands.

Tested against ProTee Labs v1.12.3 beta; should also work with stable release 1.11.6.

## License

MIT
