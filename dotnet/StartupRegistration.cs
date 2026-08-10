using System.IO;
using Microsoft.Win32;

namespace VxProxy;

/// <summary>
/// Optional "Start with Windows" registration.
///
/// Uses the per-user HKCU Run key rather than a scheduled task or the all-users
/// HKLM key: it needs no admin rights, applies only to the user who ticked it, and
/// is trivially reversible — unticking removes the value outright.
///
/// The registry is the single source of truth. We deliberately don't mirror this
/// into settings.json, because the user can also remove it from Task Manager's
/// Startup tab or Settings > Apps > Startup, and a cached copy would then disagree
/// with reality.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under the Run key. Must stay stable or we'd orphan old entries.</summary>
    private const string ValueName = "VX Connector";

    /// <summary>True if we're registered to run at login from this exact binary.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var existing = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(existing)) return false;

            // An entry pointing at a binary we've since moved is stale, not enabled.
            var target = Environment.ProcessPath;
            return string.IsNullOrEmpty(target)
                || existing.Contains(target, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-point a startup entry left behind by a binary that has since moved.
    ///
    /// <see cref="IsEnabled"/> treats an entry naming some other path as disabled, which
    /// is correct but means the option silently un-ticks itself whenever the exe moves —
    /// re-downloading to a different folder, or moving from a portable copy into the
    /// installed location. This repairs that case on startup so the user doesn't have to
    /// notice and re-tick.
    ///
    /// The guard is that the recorded path no longer exists: an entry pointing at a
    /// binary that IS still there is a genuine second install, and we must not hijack it.
    /// </summary>
    public static void RepairIfStale(Action<string>? log = null)
    {
        try
        {
            var target = Environment.ProcessPath;
            if (string.IsNullOrEmpty(target)) return;

            using var read = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (read?.GetValue(ValueName) is not string existing || existing.Length == 0) return;

            // Already us — nothing to do.
            if (existing.Contains(target, StringComparison.OrdinalIgnoreCase)) return;

            var recorded = ExtractExePath(existing);
            if (string.IsNullOrEmpty(recorded) || File.Exists(recorded)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            key.SetValue(ValueName, $"\"{target}\"", RegistryValueKind.String);
            log?.Invoke($"Startup entry named a binary that no longer exists ({recorded}) — " +
                        "re-pointed it at this copy, so \"Start with Windows\" stays on.");
        }
        catch
        {
            // Best-effort: on failure the user just has to re-tick the option.
        }
    }

    /// <summary>
    /// Pull the executable path out of a Run value. We always write a single quoted path
    /// with no arguments, but be tolerant of a hand-edited value that has some.
    /// </summary>
    private static string ExtractExePath(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('"')) return trimmed;

        var close = trimmed.IndexOf('"', 1);
        return close > 1 ? trimmed[1..close] : trimmed.Trim('"');
    }

    /// <summary>Add or remove the login entry. Returns false if the registry write failed.</summary>
    public static bool SetEnabled(bool enabled, Action<string>? log = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                log?.Invoke("Could not open the Windows startup registry key.");
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log?.Invoke("VX Connector will no longer start with Windows.");
                return true;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                log?.Invoke("Could not determine our own path — startup entry not created.");
                return false;
            }

            // Quoted because the path may contain spaces. No mode flag is passed: the
            // saved preference should decide the mode at login, exactly as it does for
            // a normal launch. Baking a flag in here would silently override the tray.
            key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            log?.Invoke("VX Connector will start with Windows.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not update the Windows startup setting: {ex.Message}");
            return false;
        }
    }
}
