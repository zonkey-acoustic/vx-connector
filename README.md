# VX Connector

An unofficial Windows helper that lets your **ProTee VX** launch monitor talk to **Infinite Tees** or **Drills** through ProTee Labs's OpenConnect path. VX Connector runs in the system tray, watches ProTee Labs's shots folder, converts each new shot to OpenConnect JSON, and forwards it to your sim over a persistent TCP connection.

```
ProTee VX  →  ProTee Labs  →  ProTeeUnited\Shots\* (on disk)
                                          ↓ (FileSystemWatcher)
                                  VX Connector  ═══►  Infinite Tees (:999) / Drills (:921)
                                                  (persistent TCP)
```

The sim target (Infinite Tees by default; Drills also supported) is selected via the tray menu.

## Quick Start

1. **Download** `vx-connector.exe` from the [latest release](https://github.com/zonkey-acoustic/itees-vx-connector/releases/latest) (or build from source — see below).

2. **Launch VX Connector** — double-click `vx-connector.exe`. The tray icon turns purple and tooltip shows *"VX Proxy — Folder watcher → InfiniteTees :999"*.

3. **Launch Infinite Tees** (or Drills — see below). Infinite Tees on its default port 999 — no configuration needed. Its UI should show "connected" because VX Connector holds a persistent TCP socket to it.

4. **Launch ProTee Labs**, go to **Game Options → Game: GSPro**, and hit **Connect**. ProTee Labs writes each shot to disk; VX Connector picks it up and forwards it to your sim.

### Drills

If you use Drills instead of Infinite Tees, right-click the tray icon and pick **"Switch to Folder Watcher → Drills"** (or launch with `vx-connector.exe --watch-drills`). Drills uses port 921 by default; no further config needed.

### CLI flags

| Flag | Mode |
|---|---|
| *(none)* | Folder Watcher → Infinite Tees (default) |
| `--watch-drills` | Folder Watcher → Drills |
| `--watch-itees` / `--watch-infinite-tees` | Folder Watcher → Infinite Tees |
| `--folder-watcher` / `-w` | Folder Watcher → Infinite Tees (back-compat alias) |

### Upgrading from an earlier release

The exe was renamed from `GSPconnect.exe` to `vx-connector.exe` in v1.3.1. The new build will detect and kill any stale `GSPconnect.exe` process from prior versions at startup (as long as it's not in the canonical GSPro install path at `C:\GSPro\Core\GSPC\`), so the old tray icon should disappear automatically.

If you ran v1.2.x or prior and moved Infinite Tees to port 921 for the old "Direct mode", run `scripts/reset-itees-port.ps1` once to put Infinite Tees back on its standard port 999 for Folder Watcher mode.

## System Tray

| Color  | Meaning                                                              |
|--------|----------------------------------------------------------------------|
| Purple | Folder Watcher mode — VX Connector is forwarding shots over a persistent TCP connection |
| Red    | Stopped                                                              |

Hover the tray icon for the current sim target and port. Double-click to open the log window. Right-click for actions:

- **Show Log** — open the log window
- **Switch to Folder Watcher → Drills** — forward shots to Drills (default port 921)
- **Switch to Folder Watcher → Infinite Tees** — forward shots to Infinite Tees (default port 999)
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

The published exe is at `dotnet/bin/Release/net8.0-windows/win-x64/publish/vx-connector.exe` — single file, no .NET runtime required on the target machine.

## How It Works

ProTee Labs writes each shot's data to `%APPDATA%\ProTeeUnited\Shots\<timestamp>\ShotData.json` after you click Connect in its UI. VX Connector watches that folder, converts the data into the **OpenConnect API** format (newline-terminated JSON over raw TCP), and forwards it to your chosen sim.

VX Connector opens a single persistent TCP connection to the sim on Start and reuses it across shots, reconnecting lazily if it drops. This keeps the sim's UI showing "connected" between shots even during long pauses (e.g. walking to the next tee), and avoids the per-shot connect/disconnect pattern that earlier versions used.

## Protocol Notes

The OpenConnect spec ([reference](https://gsprogolf.com/GSProConnectV1.html)) implies a persistent TCP connection but doesn't mandate continuous traffic between shots. In practice, ProTee Labs sends messages only around shots and never uses the spec's `IsHeartBeat` mechanism (verified across all captured sessions). GSPro keeps the wire warm by pushing `Code 201` (player context) and `Code 202` (ready) updates unprompted between shots; Infinite Tees and Drills don't push anything beyond `Code 200` acks. VX Connector's persistent connection model handles both styles — the socket stays open even when no application-layer traffic is flowing.

## Note

This unofficial integration is community-developed and provided as-is, without official support from the respective brands.

Tested against ProTee Labs v1.12.3 beta; should also work with stable release 1.11.6.

## License

MIT
