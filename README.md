# VX Connector

An unofficial Windows helper that lets your **ProTee VX** launch monitor talk to **Infinite Tees** or **Drills** through ProTee Labs's OpenConnect path. VX Connector runs in the system tray. It has two modes:

- **Folder Watcher mode (default)** — VX Connector watches ProTee Labs's shots folder, converts each new shot to OpenConnect JSON, and forwards it to your sim over a persistent TCP connection. The sim target is explicit (Infinite Tees by default; Drills also supported), so you don't have to reconfigure your simulator's listening port.
- **Direct mode** — VX Connector just runs as a process so ProTee Labs's `GSPconnect.exe` check passes. Your simulator listens on port 921 directly, and ProTee Labs talks to it without anything in the data path. Requires configuring your sim to use port 921.

```
Folder Watcher mode (default):
ProTee VX  →  ProTee Labs  →  ProTeeUnited\Shots\* (on disk)
                                          ↓ (FileSystemWatcher)
                                  VX Connector  ═══►  Infinite Tees (:999) / Drills (:921)
                                                  (persistent TCP)

Direct mode:
ProTee VX  →  ProTee Labs Software  →  Infinite Tees / Drills (:921)
                                  ↑
                                  │ checks for GSPconnect.exe to be running
                                  │
                              VX Connector (idle, just present)
```

## Quick Start

1. **Download** `GSPconnect.exe` from the [latest release](https://github.com/zonkey-acoustic/itees-vx-connector/releases/latest) (or build from source — see below).

2. **Launch VX Connector first** — double-click `GSPconnect.exe`. It starts in Folder Watcher → Infinite Tees mode by default. Tray icon turns purple and tooltip shows *"VX Proxy — Folder watcher → InfiniteTees :999"*.

3. **Launch Infinite Tees** (or Drills — see below). Infinite Tees on its default port 999 — no configuration needed. Its UI should show "connected" because VX Connector holds a persistent TCP socket to it.

4. **Launch ProTee Labs**, go to **Game Options → Game: GSPro**, and hit **Connect**. ProTee Labs writes each shot to disk; VX Connector picks it up and forwards it to your sim.

> **Important:** VX Connector must be running *before* you click Connect in ProTee Labs. ProTee Labs's pre-flight check looks for a `GSPconnect.exe` process before opening a connection.

### Drills

If you use Drills instead of Infinite Tees, right-click the tray icon and pick **"Switch to Folder Watcher → Drills"** (or launch with `GSPconnect.exe --watch-drills`). Drills uses port 921 by default; no further config needed.

### Direct mode (alternate path)

If you'd rather have ProTee Labs talk straight to your sim with nothing in the data path:

1. **Configure your simulator to listen on port 921:**
   - **Infinite Tees** — right-click VX Connector's tray icon and pick **"Switch Infinite Tees to Direct mode (port 921)"**, then restart Infinite Tees. (You can also run `scripts/set-itees-port.ps1` manually.)
   - **Drills** — Drills's helper already listens on port 921 by default.

2. **Switch VX Connector to Direct mode** via the tray menu (or launch with `GSPconnect.exe --direct`). Tray icon turns blue.

Direct mode trades a small UI quirk (your sim may briefly show "not connected" between holes — see [Protocol Notes](#protocol-notes)) for keeping VX Connector out of the data path entirely.

### CLI flags

| Flag | Mode |
|---|---|
| *(none)* | Folder Watcher → Infinite Tees (default) |
| `--direct` | Direct |
| `--watch-drills` | Folder Watcher → Drills |
| `--watch-itees` / `--watch-infinite-tees` | Folder Watcher → Infinite Tees |
| `--folder-watcher` / `-w` | Folder Watcher → Infinite Tees (back-compat alias) |

### Upgrading from an earlier release

Earlier releases defaulted to Direct mode and required Infinite Tees on port 921. The current default is Folder Watcher → Infinite Tees on 999, so no port change is needed if you're starting fresh. If you previously moved Infinite Tees to 921 and want to revert, run `scripts/reset-itees-port.ps1` once.

## System Tray

| Color  | Meaning                                                              |
|--------|----------------------------------------------------------------------|
| Blue   | Direct mode — VX Connector is idle; ProTee Labs talks to the sim directly |
| Purple | Folder Watcher mode — VX Connector is forwarding shots over a persistent TCP connection |
| Red    | Stopped                                                              |

Hover the tray icon for the current sim target and port. Double-click to open the log window. Right-click for the mode switcher and other actions:

- **Show Log** — open the log window
- **Switch to Direct mode** — VX Connector goes idle; ProTee Labs talks straight to the sim
- **Switch to Folder Watcher → Drills** — forward shots to Drills (default port 921)
- **Switch to Folder Watcher → Infinite Tees** — forward shots to Infinite Tees (default port 999)
- **Switch Infinite Tees to Direct mode (port 921)** — only visible when Infinite Tees is currently on 999; used to prep for Direct mode
- **Quit** — exit VX Connector

The currently-active mode is hidden from the switcher list.

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

- **Folder Watcher mode**: VX Connector watches the folder ProTee Labs writes each shot's `ShotData.json` into, converts that data to the OpenConnect format, and forwards it to your chosen sim. It opens a single persistent TCP connection to the sim on Start and reuses it across shots, reconnecting lazily if it drops. This keeps the sim's UI showing "connected" between shots even during long pauses.
- **Direct mode**: VX Connector binds no port and forwards nothing. Your sim listens on port 921 and receives ProTee Labs's traffic directly.

VX Connector's executable is named and version-stamped to match `C:\GSPro\Core\GSPC\GSPconnect.exe` so ProTee Labs's process-name gate accepts it. It does not impersonate the licensed GSPro Connect for any deeper purpose; ProTee Labs routes to a separate, unauthenticated code path when our impersonator is present.

## Protocol Notes

The OpenConnect spec ([reference](https://gsprogolf.com/GSProConnectV1.html)) implies a persistent TCP connection but doesn't mandate continuous traffic. In practice, ProTee Labs sends messages only around shots and never uses the spec's `IsHeartBeat` mechanism (verified across all captured sessions). GSPro keeps the wire warm by pushing `Code 201`/`Code 202` updates unprompted; Infinite Tees does not, which is why Infinite Tees's "connected" UI can briefly read "not connected" between holes in Direct mode (when ProTee Labs's persistent socket is the only liveness signal and it's been idle).

Folder Watcher mode sidesteps this by holding its own persistent socket — VX Connector's connection stays open even when ProTee Labs is silent, and the sim's UI stays correct.

## Note

This unofficial integration is community-developed and provided as-is, without official support from the respective brands.

Tested against ProTee Labs v1.12.3 beta; should also work with stable release 1.11.6.

## License

MIT
