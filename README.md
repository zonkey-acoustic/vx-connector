# VX Connector

An unofficial Windows helper that lets your **ProTee VX** launch monitor talk to **Infinite Tees**, **Drills** or **ShotForge**. VX Connector runs in the system tray, receives shots directly from ProTee Labs, and forwards them to your sim over a persistent TCP connection.

```
ProTee VX  →  ProTee Labs  ──:12321──►  VX Connector  ═══►  Infinite Tees (:999) / Drills, ShotForge (:921)
                                                        (persistent TCP)
```

The sim target (Infinite Tees or OpenAPI Compatible, which covers Drills, ShotForge and other OpenAPI-compatible sims) is selected via the tray menu. On first launch VX Connector picks Infinite Tees if it finds it on the PC, and OpenAPI Compatible otherwise. After that it remembers your last choice.

> **On ProTee Labs 1.12.9.0 or newer, you need VX Connector v1.4.0+.** That ProTee Labs
> release stopped writing `ShotData.json` and removed the OpenAPI (:921) connector, which
> broke every earlier version of VX Connector. If you're still on an older ProTee Labs
> build, either version works — see [Legacy folder-watcher mode](#legacy-folder-watcher-mode).

## Quick Start

1. **Install** — download `VxConnectorSetup.exe` from the [latest release](https://github.com/zonkey-acoustic/vx-connector/releases/latest) and run it. It installs for your user only, so there's no admin prompt, and it adds a Start Menu shortcut plus an entry in **Settings → Apps** for clean removal. See [Installing](#installing) for the portable option and for what to expect from SmartScreen.

2. **Launch VX Connector** — from the Start Menu, or double-click `vx-connector.exe`. The tray icon turns purple and the tooltip shows *"VX Connector — Proxy → InfiniteTees :999 (waiting for ProTee Labs)"*. It turns **green** once both ProTee Labs and your sim are connected.

3. **Launch Infinite Tees** (or Drills / ShotForge — see below) on its default port 999 — no configuration needed.

   Infinite Tees only opens that port once you're in a **round or practice range**. Until
   then the tray stays purple and Infinite Tees shows "not connected" — that's expected.
   VX Connector retries in the background, so it connects on its own the moment the port
   opens; you don't need to restart it or start things in a particular order.

   **Your sim stays "not connected" until ProTee Labs is connected too**, even if it's
   already in a round. That's deliberate: your sim decides whether to show *ready* purely
   from whether something is holding the connection open, so VX Connector holds it open
   only while a shot could actually get through. If it connected the moment it started,
   your sim would sit at **READY** with ProTee Labs offline, and you'd only find out by
   swinging.

4. **Launch ProTee Labs**, go to **Game Options → Game: GSPro**, and hit **Connect**.

   ProTee Labs will warn that *"connecting through the OpenAPI connector is no longer
   supported"*. **Dismiss it — shots flow normally.** The warning is cosmetic; it appears
   because VX Connector isn't the licensed GSPro Connect binary.

You do **not** need GSPro installed, and you do not need a GSPro licence.

### OpenAPI Compatible (Drills, ShotForge)

If you use Drills, ShotForge or another OpenAPI-compatible sim instead of Infinite Tees, right-click the tray icon and pick **"Switch to OpenAPI Compatible"** (or launch with `vx-connector.exe --proxy-drills`). Shots go to port 921, which is the default for both Drills and ShotForge, so no further config is needed. If Drills is installed and configured for a different port, VX Connector picks that up from its `settings.json`. Any other OpenAPI-compatible sim needs to listen for OpenConnect on port 921.

## Installing

Two options — pick one:

| | `VxConnectorSetup.exe` | `vx-connector.exe` (portable) |
|---|---|---|
| Where it goes | `%LOCALAPPDATA%\Programs\VxConnector` | wherever you put it |
| Admin needed | No | No |
| Start Menu shortcut | Yes | No |
| Listed in Settings → Apps | Yes | No |
| Updates itself | Yes | Yes |

Both are the same application; the installer just puts it somewhere sensible and gives you a
clean uninstall. Nothing is installed for other users, and no service or scheduled task is
created.

**SmartScreen will warn you on first run.** The binaries aren't code-signed — a certificate
costs real money for a free tool — so Windows shows *"Windows protected your PC"*. Choose
**More info → Run anyway**. If you'd rather verify what you downloaded, every release also
carries a `.sha256` file for each binary; compare it against
`Get-FileHash .\VxConnectorSetup.exe -Algorithm SHA256`.

Some antivirus products also object, because VX Connector creates a copy of itself named
`GSPconnect.exe` (see [How It Works](#how-it-works)). Allowlist the install folder if that
happens.

### Updating

VX Connector checks GitHub for a newer release shortly after it starts and every six hours
after that. It never installs anything on its own — when it finds one you'll get a tray
notification and an **"Update to v…"** item at the top of the right-click menu. Click it and
VX Connector downloads the new version, verifies its SHA-256, replaces itself and restarts,
keeping your current sim target.

Don't do it mid-shot: forwarding pauses for a few seconds while it restarts.

You can also check on demand with **Check for Updates…**, and turn the automatic check off
entirely with **Automatically Check for Updates**. That check is the only time VX Connector
talks to anything outside your machine — everything else is a loopback connection to ProTee
Labs and your sim. It sends nothing but the request itself.

The checksum verification protects you against a truncated or corrupted download. It is not a
signature: the hash comes from the same GitHub release as the binary, so it can't tell you the
release itself is genuine. Judge that the way you judged the download you're reading this from.

### Uninstalling

Installer: **Settings → Apps → VX Connector → Uninstall**, or the Start Menu's *Uninstall VX
Connector*. That removes the exe, the `GSPconnect.exe` stub, the shortcuts and the startup
entry.

Portable: quit VX Connector, delete the exe, untick **Start with Windows** first (or delete the
`VX Connector` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).

Either way your preferences stay in `%APPDATA%\VxConnector\settings.json` — delete that folder
too if you want no trace left.

## System Tray

| Color  | Meaning                                                           |
|--------|-------------------------------------------------------------------|
| Green  | Connected end to end — ProTee Labs **and** your sim               |
| Purple | Proxy running, but one side isn't connected yet                   |
| Teal   | Legacy folder-watcher mode (pre-beta ProTee Labs only)            |
| Red    | Stopped                                                           |

Purple isn't an error — it's the normal state until both ends are up. Hover the tray icon for the current sim target and port; when it's purple the tooltip names which side is still missing. Double-click to open the log window.

Purple always means your sim shows "not connected" as well: while ProTee Labs is away, VX Connector deliberately closes its connection to the sim rather than leave it advertising **READY** for shots that can't arrive. Green in the tray and *connected* in the sim mean the same thing, so you can trust either one.

Right-click for actions:

- **Show Log** — open the log window
- **Check for Updates…** — ask GitHub now instead of waiting for the next scheduled check
- **Switch to OpenAPI Compatible** — forward shots to Drills, ShotForge or another OpenAPI-compatible sim (default port 921)
- **Switch to Infinite Tees** — forward shots to Infinite Tees (default port 999)
- **Legacy (pre-beta ProTee Labs)** — the old folder-watcher modes
- **Start with Windows** — launch VX Connector automatically at login
- **Automatically Check for Updates** — turn the six-hourly check on or off
- **Quit** — exit VX Connector

An **Update to v…** item appears above the separator when a newer release is available — see
[Updating](#updating).

The currently-active mode is hidden from the switcher list. Your choice is remembered across launches.

The menu header shows the running version. It also appears in the log window's title bar and as the first line of the log, which is worth including if you report a problem.

### Start with Windows

Ticking **Start with Windows** makes VX Connector launch at login so it's always ready. It adds a per-user entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — no admin rights needed, and unticking removes it.

The startup entry doesn't pin a mode, so VX Connector uses your saved sim choice at login just as it would on a normal launch.

If you move `vx-connector.exe`, the entry still names the old location. VX Connector repairs
that on startup — if the recorded path no longer exists, it re-points the entry at itself, so
switching from a portable copy to the installer keeps the setting. An entry naming a copy that
*is* still there is left alone, since that's a second install rather than a stale entry; in
that case re-tick the option to claim it.

You can also turn this off from Task Manager's **Startup apps** tab; the tray menu reads the real registry state each time it opens, so it stays accurate either way.

VX Connector also enforces single-instance: launching it again will kill any prior instance running from the same binary path.

## Requirements

- Windows 10/11 (x64)
- ProTee Labs with a ProTee VX
- Infinite Tees, Drills, ShotForge, or another OpenAPI-compatible sim

No .NET install needed — the release exe is self-contained. GSPro is **not** required. An
internet connection is optional: it's used only for the update check.

## Building

```bash
cd dotnet
dotnet publish -c Release
# Output: bin/Release/net8.0-windows/win-x64/publish/vx-connector.exe
```

The build produces a single self-contained, compressed exe (~68 MB). At runtime VX Connector
hard-links a second name (`GSPconnect.exe`) beside it, which shares the same file data and
costs no extra disk.

To build the installer you also need [Inno Setup 6](https://jrsoftware.org/isdl.php):

```bash
mkdir -p installer/payload
cp dotnet/bin/Release/net8.0-windows/win-x64/publish/vx-connector.exe installer/payload/
"/c/Program Files (x86)/Inno Setup 6/ISCC.exe" //DMyAppVersion=1.5.4 installer/vx-connector.iss
# Output: dist/VxConnectorSetup.exe
```

Releases are cut by pushing a `v*.*.*` tag; the workflow refuses to publish if the tag and the
csproj `<Version>` disagree. Both the exe and the installer are built, checksummed and attached
automatically.

## Note

This unofficial integration is community-developed and provided as-is, without official support from the respective brands.

## License

MIT
