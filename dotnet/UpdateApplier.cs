using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace VxProxy;

/// <summary>
/// Downloads a release asset, verifies it, and replaces our own exe with it.
///
/// One code path serves both the installed and the portable copy. That works because the
/// installer is per-user (<c>%LOCALAPPDATA%\Programs\VxConnector</c>), so the install
/// directory is writable by the process that lives in it — no elevation, no helper
/// service, no separate updater binary.
///
/// The swap itself relies on Windows allowing a *running* executable to be renamed: the
/// image is locked against writes and deletes, but not against a rename within the same
/// volume. So we move ourselves aside, drop the new binary into the vacated path, and
/// relaunch. <see cref="Program"/> deletes the aside copy on the next start.
///
/// Two things have to happen before the swap, both to do with the GSPconnect gate:
/// the stub child holds a hard link to our exe and must be stopped, and the staged
/// GSPconnect.exe must be removed so the next launch re-links it against the NEW binary.
/// </summary>
public static class UpdateApplier
{
    /// <summary>
    /// Inno Setup's per-user uninstall key, derived from the AppId in
    /// installer/vx-connector.iss. Only used to keep the Add/Remove Programs version
    /// honest after a self-update; absent for portable copies, which is fine.
    /// </summary>
    private const string InnoUninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3A7C21-5D64-4E9B-9C0A-2B7E14D6A3F5}_is1";

    private const string StubExeName = "GSPconnect.exe";

    /// <summary>
    /// Downloads land here, deliberately NOT in the install directory: a partial or
    /// abandoned download sitting next to our exe could be picked up as a staged stub by
    /// GspConnectStub's length+timestamp freshness check.
    /// </summary>
    internal static string StagingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VxConnector", "update");

    /// <summary>
    /// Fetch, verify and install <paramref name="update"/>. Returns true when the exe on
    /// disk is the new version and the caller should shut down and relaunch via
    /// <see cref="RestartInto"/>. On false, nothing was changed — the running install is
    /// intact and the reason is in the log.
    /// </summary>
    public static async Task<bool> ApplyAsync(
        AvailableUpdate update, Action<string> log, CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            log("Update Error: cannot locate our own exe.");
            return false;
        }

        var installDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(installDir))
        {
            log("Update Error: cannot determine our own directory.");
            return false;
        }

        // A per-machine install (via the installer's elevation prompt) or a portable copy
        // dropped somewhere protected can't rewrite itself. Hand the user off rather than
        // failing halfway through a swap.
        if (!IsWritable(installDir))
        {
            log($"Cannot update in place — {installDir} is not writable. " +
                "Opening the release page so you can install it manually.");
            OpenInBrowser(update.ReleaseUrl, log);
            return false;
        }

        string staged;
        try
        {
            Directory.CreateDirectory(StagingDirectory);
            staged = Path.Combine(StagingDirectory, $"vx-connector-{update.Version}.exe");

            using var http = GitHubHttp.Create(TimeSpan.FromMinutes(30));
            await DownloadAsync(http, update.ExeUrl, staged, log, ct);

            var expected = await FetchExpectedHashAsync(http, update.Sha256Url, ct);
            var actual = ComputeSha256(staged);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                log($"Update Error: checksum mismatch — expected {expected}, got {actual}. " +
                    "Discarding the download; nothing was changed.");
                TryDelete(staged);
                return false;
            }

            log($"Verified v{update.Version} (sha256 {actual[..12]}…).");
        }
        catch (OperationCanceledException)
        {
            log("Update cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            log($"Update Error: download failed — {ex.Message}");
            return false;
        }

        // The stub is a live process running a hard link to the exe we're about to move,
        // and its staged copy must be rebuilt against the new binary. Stopping it here
        // means the delete below succeeds, rather than falling into GspConnectStub's
        // "in use, keep the stale one" branch on the next launch.
        log("Stopping the GSPconnect stub so the exe can be replaced.");
        GspConnectStub.Stop();
        TryDelete(Path.Combine(installDir, StubExeName));

        string aside;
        try
        {
            aside = ReserveAsidePath(exePath);
            File.Move(exePath, aside);
        }
        catch (Exception ex)
        {
            log($"Update Error: could not move the running exe aside — {ex.Message}");
            RestoreStub(log);
            return false;
        }

        try
        {
            File.Move(staged, exePath);
        }
        catch (Exception ex)
        {
            log($"Update Error: could not put the new exe in place — {ex.Message}");
            try
            {
                File.Move(aside, exePath);
                log("Rolled back — the version you were running is intact.");
                RestoreStub(log);
            }
            catch (Exception rollback)
            {
                log($"Update Error: rollback failed too ({rollback.Message}). " +
                    $"The previous exe is at {aside} — rename it back, or reinstall from " +
                    $"{update.ReleaseUrl}");
            }
            return false;
        }

        SyncArpVersion(installDir, update.Version, log);
        log($"Installed v{update.Version}. Restarting…");
        return true;
    }

    /// <summary>
    /// Relaunch the freshly-installed exe. Call only after the engine has stopped, so
    /// :12321 and the sim socket are free before the replacement tries to bind them.
    ///
    /// The running mode is passed as a CLI flag rather than left to the saved preference:
    /// a session started from a flag would otherwise silently switch modes across an
    /// update. Flags are one-shot and don't touch settings.json, so this changes nothing
    /// permanent.
    /// </summary>
    public static void RestartInto(EngineMode mode, Action<string> log)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = Program.CliFlagFor(mode) ?? string.Empty,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            log($"Update Error: the new version is installed but wouldn't start — {ex.Message}. " +
                "Launch VX Connector again manually.");
        }
    }

    /// <summary>
    /// Remove leftovers from a previous self-update: our aside copies, and the download
    /// staging directory. Safe to call at startup — by then the process that held them is
    /// gone (the single-instance kill in <see cref="Program"/> guarantees it).
    /// </summary>
    public static void CleanUpLeftovers()
    {
        var exePath = Environment.ProcessPath;
        var dir = Path.GetDirectoryName(exePath);

        if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(exePath))
        {
            try
            {
                var pattern = Path.GetFileName(exePath) + ".old*";
                foreach (var stale in Directory.EnumerateFiles(dir, pattern)) TryDelete(stale);
            }
            catch
            {
                // Enumeration failed — a leftover file must never block launch.
            }
        }

        try
        {
            if (Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, recursive: true);
        }
        catch
        {
            // Still in use or permission denied; it'll be reused on the next update anyway.
        }
    }

    /// <summary>
    /// Bring the GSPconnect stub back after an update aborted past the point where we
    /// stopped it. Nothing else re-ensures it — ProxyEngine only calls
    /// <see cref="GspConnectStub.Ensure"/> from Start — so without this a failed update
    /// would silently close ProTee Labs's :12321 lane for the rest of the session, and the
    /// user's only clue would be shots stopping.
    /// </summary>
    private static void RestoreStub(Action<string> log)
    {
        log("Update aborted — restarting the GSPconnect stub to reopen ProTee Labs's lane.");
        GspConnectStub.Ensure(log);
    }

    private static async Task DownloadAsync(
        HttpClient http, string url, string destination, Action<string> log, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        log(total > 0
            ? $"Downloading update ({total / 1024.0 / 1024.0:F0} MB)…"
            : "Downloading update…");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long received = 0;
        int lastDecile = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            if (total <= 0) continue;

            // Every 10% — enough to show progress on a ~78 MB download without
            // flooding a 500-line log buffer.
            var decile = (int)(received * 10 / total);
            if (decile > lastDecile)
            {
                lastDecile = decile;
                log($"  …{decile * 10}%");
            }
        }
    }

    /// <summary>
    /// The .sha256 asset body is "&lt;lowercase hash&gt;  vx-connector.exe" (two spaces),
    /// as written by the release workflow's Checksum step.
    /// </summary>
    private static async Task<string> FetchExpectedHashAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        var body = await http.GetStringAsync(url, ct);
        var token = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token ?? string.Empty;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Probe rather than infer: ACLs, Controlled Folder Access and read-only install
    /// locations all produce the same answer, and only a real write tells us.
    /// </summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".vxc-write-probe-{Environment.ProcessId}");
            using var _ = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pick a free "moved aside" path. Normally ".old"; if a previous update left one
    /// behind and it's still locked, number it rather than giving up on the update.
    /// </summary>
    private static string ReserveAsidePath(string exePath)
    {
        var first = exePath + ".old";
        if (!File.Exists(first) || TryDelete(first)) return first;

        for (int i = 1; i < 100; i++)
        {
            var candidate = $"{exePath}.old.{i}";
            if (!File.Exists(candidate) || TryDelete(candidate)) return candidate;
        }

        throw new IOException("No free path to move the current exe aside.");
    }

    /// <summary>
    /// Keep the Add/Remove Programs version honest after an in-place update, so Settings
    /// doesn't keep reporting the version the installer originally wrote. Skipped silently
    /// for portable copies, which have no uninstall key.
    /// </summary>
    private static void SyncArpVersion(string installDir, Version version, Action<string> log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InnoUninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0) return;

            var recorded = Path.TrimEndingDirectorySeparator(location.Trim('"'));
            if (!string.Equals(recorded, Path.TrimEndingDirectorySeparator(installDir),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;  // the uninstall entry describes a different copy
            }

            key.SetValue("DisplayVersion", version.ToString(3), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            log($"Note: could not update the Apps & Features version ({ex.Message}). Harmless.");
        }
    }

    private static void OpenInBrowser(string url, Action<string> log)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log($"Could not open {url} — {ex.Message}");
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
