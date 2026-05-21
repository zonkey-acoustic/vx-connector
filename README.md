# VX Connector

A unofficial Windows proxy that connects your **ProTee VX** launch monitor to **Infinite Tees** by relaying shot data through the OpenConnect API protocol.

```
ProTee VX  →  ProTee Labs Software  →  VX Connector (:921)  →  Infinite Tees (:999)
```

ProTee Labs software sends shot data (ball speed, spin, launch angle, club data) over TCP port 921. VX Connector intercepts this data and forwards it to Infinite Tees on port 999. If Infinite Tees isn't running yet, the proxy responds to ProTee Labs directly so it never stalls.

## Quick Start

1. **Download** `GSPconnect.exe` from the [latest release](https://github.com/zonkey-acoustic/itees-vx-connector/releases/latest) (or build from source — see below).

2. **Launch VX Connector first** — double-click `GSPconnect.exe`. It starts in the system tray with an orange icon (waiting for connections).

3. **Launch Infinite Tees** — once it's running, VX Connector will automatically connect to it on port 999.

4. **Launch ProTee Labs** — open the ProTee VX software and go to **Game Options**, **Game:GSPro** then hit **Connect**. ProTee Labs will connect to VX Connector on port 921.

5. **Hit shots** — the tray icon turns green when both ProTee Labs and Infinite Tees are connected. Shot data flows automatically.

> **Important:** VX Connector must be running *before* you hit Connect in ProTee Labs. ProTee Labs expects something listening on port 921 when it connects.

### Upgrading from an earlier release

If you previously used an older script to change the Infinite Tees listening port to 921 for a direct connection, run `scripts/reset-itees-port.ps1` once before starting the new version:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/reset-itees-port.ps1
```

Then restart Infinite Tees. (If you skip this step VX Proxy will still work, just in passthrough mode rather than as a full proxy.)

## System Tray

The tray icon shows connection status at a glance:

| Color  | Meaning                                          |
|--------|--------------------------------------------------|
| Green  | ProTee Labs connected, Infinite Tees connected   |
| Yellow | ProTee Labs connected, Infinite Tees disconnected |
| Orange | Listening, waiting for connections                |
| Red    | Stopped                                          |

Double-click the tray icon to open the log window. Right-click for start/stop/quit.

## Build from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd dotnet
dotnet run                      # Run in development mode
dotnet publish -c Release       # Build standalone exe
```

The published exe is at `dotnet/bin/Release/net8.0-windows/win-x64/publish/GSPconnect.exe` — single file, no .NET runtime required on the target machine.

## How It Works

ProTee Labs software communicates with sim applications using the **OpenConnect API** — newline-terminated JSON over raw TCP. Each shot arrives as a sequence of messages:

1. Status: ready, ball detected
2. Ball data only (speed, spin, launch angle)
3. Ball + club data (adds club speed, attack angle, face angle, path)
4. Status: not ready

VX Connector listens on port 921 (where ProTee Labs expects to connect), logs each shot, and forwards everything to Infinite Tees on port 999. If Infinite Tees is unavailable, the proxy generates its own acknowledgement responses so ProTee Labs keeps running normally.

## Note: ## 

This unofficial integration is community-developed and provided as-is, without official support from the respective brands.

## License

MIT
