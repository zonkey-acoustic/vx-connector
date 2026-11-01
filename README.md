# VX Connector

An unofficial Windows tray app that sends shots from a **ProTee VX** launch monitor to **Infinite Tees** or any OpenAPI-compatible sim. GSPro and a GSPro licence are **not** required.

```
ProTee VX  →  ProTee Labs  ──:12321──►  VX Connector  ──►  Infinite Tees (:999) / OpenAPI-compatible sims (:921)
```

## Supported Sims

| Sim | VX Connector mode | Port | Tested |
|-----|-------------------|------|--------|
| Infinite Tees | Infinite Tees | 999 | ✅ |
| ShotForge | OpenAPI Compatible | 921 | ✅ |
| The Fitting Bay | OpenAPI Compatible | 921 | ✅ |
| Drills | OpenAPI Compatible | 921 | ✅ |
| Muni Golf | OpenAPI Compatible | 921 | ✅ |

Any other sim that accepts OpenAPI (OpenConnect) shots on port 921 should work in **OpenAPI Compatible** mode. If you've tried one that isn't listed, please open an issue and say whether it worked.

## Requirements

- Windows 10/11 (x64)
- ProTee Labs with a ProTee VX
- A sim from the [table above](#supported-sims)

## Install

Download from the [latest release](https://github.com/zonkey-acoustic/vx-connector/releases/latest):

- **`VxConnectorSetup.exe`** (recommended): installs for your user only, so there's no admin prompt. It adds a Start Menu shortcut and an uninstall entry.
- **`vx-connector.exe`**: the portable version of the same app. Run it from wherever you like.

**Windows SmartScreen will warn you on first run** because the binaries aren't code-signed. Choose **More info → Run anyway**. Each release includes `.sha256` files if you want to check your download with `Get-FileHash <file> -Algorithm SHA256`.

Some antivirus products flag VX Connector because it creates a copy of itself named `GSPconnect.exe`. ProTee Labs only opens its connection when a process with that name is running. If your antivirus blocks it, allowlist the install folder.

## Quick Start

1. **Launch VX Connector.** The tray icon turns purple.
2. **Launch your sim.** Infinite Tees only accepts connections once you're **in a round or on the practice range**. You don't need to do anything else: VX Connector keeps retrying in the background.
3. **Launch ProTee Labs**, go to **Game Options → Game: GSPro**, and click **Connect**. ProTee Labs warns that *"connecting through the OpenAPI connector is no longer supported"*. Dismiss the warning. Shots still come through.

When both ProTee Labs and your sim are connected, the tray icon turns **green** and you're ready to hit.

Start them in any order. Your sim shows "not connected" until ProTee Labs connects too. That's on purpose: it stops the sim from showing **READY** when no shot could actually reach it.

### Choosing your sim

On first launch VX Connector picks **Infinite Tees** if it's installed and **OpenAPI Compatible** if not. To change it, right-click the tray icon:

- **Switch to Infinite Tees** sends shots to port 999.
- **Switch to OpenAPI Compatible** sends shots to port 921, for Drills, ShotForge, The Fitting Bay, Muni Golf and other OpenAPI-compatible sims. If you've changed Drills's port, VX Connector reads it from Drills's settings. Other sims need to listen on 921.

VX Connector remembers your choice. You can also pick on the command line with `--proxy-itees` or `--proxy-open-api`.

## Tray Icon

| Color  | Meaning |
|--------|---------|
| Green  | ProTee Labs **and** your sim are connected |
| Purple | Running, waiting for one side. Hover over the icon to see which |
| Red    | Stopped |

Double-click the icon to open the log window. Right-click it for:

- **Show Log**
- **Switch to …**: change your sim (see above)
- **Start with Windows**: launch at login. Turn it off here or in Task Manager's Startup apps
- **Check for Updates…** / **Automatically Check for Updates**
- **Quit**

The version number is shown at the top of the menu and on the first line of the log. Please include it when you report a problem.

## Updating

VX Connector checks GitHub for new releases when it starts and every six hours after that. When an update is available, an **Update to v…** item appears at the top of the tray menu. Click it and VX Connector downloads the update, checks its SHA-256 hash, and restarts with the same sim selected. Forwarding stops for a few seconds while it restarts, so don't update mid-shot.

It never updates without your click. The update check is the only internet traffic VX Connector sends, and you can turn it off from the tray menu.

## Uninstalling

- **Installer:** go to **Settings → Apps → VX Connector → Uninstall**.
- **Portable:** untick **Start with Windows**, quit, then delete the exe and the `GSPconnect.exe` beside it.

Your settings are kept in `%APPDATA%\VxConnector`. Delete that folder as well if you want to remove everything.

## Note

This is an unofficial, community-built integration. It is provided as-is and isn't supported by ProTee, GSPro, or the sim makers.

## License

MIT
