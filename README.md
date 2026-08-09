# VX Connector

An unofficial Windows helper that lets your **ProTee VX** launch monitor talk to **Infinite Tees** or **Drills**. VX Connector runs in the system tray, receives shots directly from ProTee Labs, and forwards them to your sim over a persistent TCP connection.

```
ProTee VX  →  ProTee Labs  ──:12321──►  VX Connector  ═══►  Infinite Tees (:999) / Drills (:921)
                                                        (persistent TCP)
```

The sim target (Infinite Tees by default; Drills also supported) is selected via the tray menu.

> **On ProTee Labs 1.12.9.0 or newer, you need VX Connector v1.4.0+.** That ProTee Labs
> release stopped writing `ShotData.json` and removed the OpenAPI (:921) connector, which
> broke every earlier version of VX Connector. If you're still on an older ProTee Labs
> build, either version works — see [Legacy folder-watcher mode](#legacy-folder-watcher-mode).

## Quick Start

1. **Download** `vx-connector.exe` from the [latest release](https://github.com/zonkey-acoustic/itees-vx-connector/releases/latest) (or build from source — see below).

2. **Launch VX Connector** — double-click `vx-connector.exe`. The tray icon turns purple and the tooltip shows *"VX Connector — Proxy → InfiniteTees :999"*.

3. **Launch Infinite Tees** (or Drills — see below) on its default port 999 — no configuration needed.

   Infinite Tees only opens that port once you're in a **round or practice range**. Until
   then VX Connector's tray sits amber and Infinite Tees shows "not connected" — that's
   expected. Once you're in, it should show connected, because VX Connector holds a
   persistent TCP socket to it.

4. **Launch ProTee Labs**, go to **Game Options → Game: GSPro**, and hit **Connect**.

   ProTee Labs will warn that *"connecting through the OpenAPI connector is no longer
   supported"*. **Dismiss it — shots flow normally.** The warning is cosmetic; it appears
   because VX Connector isn't the licensed GSPro Connect binary.

You do **not** need GSPro installed, and you do not need a GSPro licence.

### Drills

If you use Drills instead of Infinite Tees, right-click the tray icon and pick **"Switch to Drills"** (or launch with `vx-connector.exe --proxy-drills`). Drills uses port 921 by default; no further config needed.

## System Tray

| Color  | Meaning                                                           |
|--------|-------------------------------------------------------------------|
| Purple | Forwarding shots over a persistent TCP connection                 |
| Teal   | Legacy folder-watcher mode (pre-beta ProTee Labs only)            |
| Red    | Stopped                                                           |

Hover the tray icon for the current sim target and port. Double-click to open the log window. Right-click for actions:

- **Show Log** — open the log window
- **Switch to Drills** — forward shots to Drills (default port 921)
- **Switch to Infinite Tees** — forward shots to Infinite Tees (default port 999)
- **Legacy (pre-beta ProTee Labs)** — the old folder-watcher modes
- **Start with Windows** — launch VX Connector automatically at login
- **Quit** — exit VX Connector

The currently-active mode is hidden from the switcher list. Your choice is remembered across launches.

### Start with Windows

Ticking **Start with Windows** makes VX Connector launch at login so it's always ready. It adds a per-user entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — no admin rights needed, and unticking removes it.

The startup entry doesn't pin a mode, so VX Connector uses your saved sim choice at login just as it would on a normal launch. If you move or reinstall `vx-connector.exe`, the entry points at the old location — re-tick the option to point it at the new one.

You can also turn this off from Task Manager's **Startup apps** tab; the tray menu reads the real registry state each time it opens, so it stays accurate either way.

VX Connector also enforces single-instance: launching it again will kill any prior instance running from the same binary path.

## How It Works

ProTee Labs sends shot data to a golf sim over the **OpenConnect API** — JSON over raw TCP. VX Connector stands in as that sim: it listens on port **12321** (the lane ProTee Labs uses for its "ProTee VX Connector" game connection), accepts the connection, and relays each shot to Infinite Tees or Drills.

Two details make this work:

- **ProTee Labs only opens the lane if a process named `GSPconnect.exe` exists.** VX Connector satisfies that check itself by hard-linking its own executable under that name and running it as an idle stub process, which it cleans up on exit. There's no licence check involved — any process with that name works, and the real GSPro never needs to be installed. If the genuine GSPconnect is already running, VX Connector leaves it alone.
- **ProTee Labs doesn't newline-terminate on this lane**, but the sims require it. Messages arrive as bare back-to-back JSON objects, so VX Connector frames them by brace depth and adds the delimiter when forwarding. Shot data is otherwise passed through verbatim.

VX Connector opens a single persistent TCP connection to the sim on Start and reuses it across shots, reconnecting lazily if it drops. This keeps the sim's UI showing "connected" between shots even during long pauses (e.g. walking to the next tee). Acknowledgements from the sim are relayed back to ProTee Labs so it also sees a healthy connection.

### Legacy folder-watcher mode

Older ProTee Labs builds wrote each shot to `%APPDATA%\ProTeeUnited\Shots\<timestamp>\ShotData.json`, and VX Connector watched that folder. ProTee Labs **1.12.9.0** stopped writing that file — shots now go to an encrypted local database — so this mode no longer receives anything on current builds.

It's kept for anyone still on an older ProTee Labs, under the tray menu's **Legacy** submenu or via `--watch-itees` / `--watch-drills`.

## Troubleshooting

Open the log window (double-click the tray icon) first — it names most of these directly.

**ProTee Labs says "Connection to GSPro failed, attempting to launch game"**
ProTee Labs can't see a `GSPconnect.exe` process, so it won't open the lane. Check the log
for *"Started GSPconnect stub"*. If it says the stub exited immediately, your antivirus or
an application-allowlisting policy most likely blocked it — VX Connector creates a copy of
itself named `GSPconnect.exe`, which some scanners treat as suspicious. Allowlist the
VX Connector folder.

If ProTee Labs then tries to *launch* GSPro and hangs, close any already-running GSPro
first — it waits for a process-start event that never fires if the game is already up.

**"Port 12321 is already in use"**
The licensed GSPro Connect is running and owns the port. Close GSPro and GSPconnect, then
restart VX Connector. The two can't share the lane.

**ProTee Labs connects, but no shots arrive**
Check the game target is set to **GSPro** in Game Options. Other targets (E6, TGC,
GolfCore, ProTee Play) use different protocols or need a ProTee Labs licence, and won't
reach VX Connector.

**Infinite Tees shows its connection as "Stopped"**
Infinite Tees binds port 999 once at startup and doesn't retry. If anything else held the
port at that moment, it silently parks in Stopped and no shots will ever land. Free the
port and restart Infinite Tees. Note it also doesn't open the port until you're in a
round or practice range — until then VX Connector's tray sits amber, which is expected.

**Shots arrive but the sim doesn't react**
The log will show `SHOT #n [Proxy] …` followed by a `Server:` line. No `Server:` line means
the sim isn't accepting — confirm it's running and on the expected port (Infinite Tees 999,
Drills 921), and that the port matches what the tray tooltip shows.

**Nothing at all happens after a shot**
If you're on ProTee Labs 1.12.9.0+ and the tray is teal, you're in legacy folder-watcher
mode, which no longer receives anything. Switch to a proxy mode from the tray menu.

## Command Line

| Flag | Effect |
|------|--------|
| *(none)* | Last-used mode, else Proxy → Infinite Tees |
| `--proxy-itees` | Proxy → Infinite Tees |
| `--proxy-drills` | Proxy → Drills |
| `--watch-itees` | Legacy folder watcher → Infinite Tees |
| `--watch-drills` | Legacy folder watcher → Drills |

CLI flags are one-shot overrides and don't change your saved preference; use the tray menu for that.

## Requirements

- Windows 10/11 (x64)
- ProTee Labs with a ProTee VX
- Infinite Tees or Drills

No .NET install needed — the release exe is self-contained. GSPro is **not** required.

## Building

```bash
cd dotnet
dotnet publish -c Release
# Output: bin/Release/net8.0-windows/win-x64/publish/vx-connector.exe
```

The build produces a single self-contained exe. At runtime VX Connector hard-links a second
name (`GSPconnect.exe`) beside it, which shares the same file data and costs no extra disk.

## Note

This unofficial integration is community-developed and provided as-is, without official support from the respective brands.

## License

MIT
