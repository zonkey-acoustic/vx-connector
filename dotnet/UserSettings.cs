using System.IO;
using System.Text.Json;

namespace VxProxy;

/// <summary>
/// Persists VX Connector's own preferences (currently just the last-used mode).
/// Read on startup when no CLI flag is given; written when the user switches
/// modes via the tray menu. CLI flags are intentionally transient and never
/// touch this file — they're one-shot overrides.
/// </summary>
public static class UserSettings
{
    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VxConnector", "settings.json");

    public static EngineMode? LoadLastMode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
            if (doc.RootElement.TryGetProperty("lastMode", out var v)
                && v.ValueKind == JsonValueKind.String
                && Enum.TryParse<EngineMode>(v.GetString(), out var mode))
            {
                return mode;
            }
        }
        catch
        {
            // Missing, locked, malformed — treat as no saved preference.
        }
        return null;
    }

    public static void SaveLastMode(EngineMode mode)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                new { lastMode = mode.ToString() },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort: writes that fail (e.g. permission denied) shouldn't
            // crash the app — the user just won't get sticky behavior.
        }
    }
}
