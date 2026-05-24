# VX Connector

An unofficial Windows helper that lets your **ProTee VX** launch monitor talk to **Infinite Tees** or **Drills** via the OpenAPI protocol. VX Connector runs in the system tray, watches ProTee Labs's shots folder, converts each new shot to OpenAPI JSON, and forwards it to your sim over a persistent TCP connection.

```
ProTee VX  →  ProTee Labs  →  ProTeeUnited\Shots\* (on disk)
                                          ↓ (FileSystemWatcher)
                                  VX Connector  ═══►  Infinite Tees (:999) / Drills (:921)
                                                  (persistent TCP)
```

The sim target (Infinite Tees by default; Drills also supported) is selected via the tray menu.

## Quick Start

1. **Download** `vx-connector.exe` from the [latest release](https://github.com/zonkey-acoustic/itees-vx-connector/releases/latest) (or build from source — see below).

2. **Launch VX Connector** — double-click `vx-connector.exe`. The tray icon turns purple and tooltip shows *"VX Connector — InfiniteTees :999"*.

3. **Launch Infinite Tees** (or Drills — see below). Infinite Tees on its default port 999 — no configuration needed. Its UI should show "connected" because VX Connector holds a persistent TCP socket to it.

4. **Launch ProTee Labs**, go to **Game Options → Game: GSPro**, and hit **Connect**. ProTee Labs writes each shot to disk; VX Connector picks it up and forwards it to your sim.

### Drills

If you use Drills instead of Infinite Tees, right-click the tray icon and pick **"Switch to Drills"** (or launch with `vx-connector.exe --watch-drills`). Drills uses port 921 by default; no further config needed.

## System Tray

| Color  | Meaning                                                              |
|--------|----------------------------------------------------------------------|
| Purple | VX Connector is forwarding shots over a persistent TCP connection |
| Red    | Stopped                                                              |

Hover the tray icon for the current sim target and port. Double-click to open the log window. Right-click for actions:

- **Show Log** — open the log window
- **Switch to Drills** — forward shots to Drills (default port 921)
- **Switch to Infinite Tees** — forward shots to Infinite Tees (default port 999)
- **Quit** — exit VX Connector

The currently-active mode is hidden from the switcher list.

VX Connector also enforces single-instance: launching it again will kill any prior instance running from the same binary path.

## How It Works

ProTee Labs writes each shot's data to `%APPDATA%\ProTeeUnited\Shots\<timestamp>\ShotData.json` after you click Connect in its UI. VX Connector watches that folder, converts the data into the **OpenConnect API** format (newline-terminated JSON over raw TCP), and forwards it to your chosen sim.

VX Connector opens a single persistent TCP connection to the sim on Start and reuses it across shots, reconnecting lazily if it drops. This keeps the sim's UI showing "connected" between shots even during long pauses (e.g. walking to the next tee), and avoids the per-shot connect/disconnect pattern that earlier versions used.

## Note

This unofficial integration is community-developed and provided as-is, without official support from the respective brands.

## License

MIT
