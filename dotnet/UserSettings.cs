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
    /// <summary>
    /// Bumped when a saved value needs reinterpreting. Files written before this
    /// existed (v1.3.3 and earlier) have no "schema" key and are treated as 1.
    /// </summary>
    private const int CurrentSchema = 2;

    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VxConnector", "settings.json");

    public static EngineMode? LoadLastMode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
            var root = doc.RootElement;

            if (!root.TryGetProperty("lastMode", out var v)
                || v.ValueKind != JsonValueKind.String
                || !Enum.TryParse<EngineMode>(v.GetString(), out var mode))
            {
                return null;
            }

            int schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32()
                : 1;

            if (schema < CurrentSchema)
            {
                mode = MigrateFromSchema1(mode);
                SaveLastMode(mode);
            }

            return mode;
        }
        catch
        {
            // Missing, locked, malformed — treat as no saved preference.
        }
        return null;
    }

    /// <summary>
    /// Everyone upgrading from v1.3.3 or earlier has a FolderWatcher mode saved,
    /// because that was the only forwarding mode. On ProTee Labs 1.12.9.0+ that mode
    /// receives nothing at all, so honouring it would leave upgraders silently dead —
    /// move them to the equivalent proxy target instead.
    ///
    /// Only applied once. After migration the file carries the current schema, so a
    /// user who deliberately picks legacy mode afterwards keeps it.
    /// </summary>
    private static EngineMode MigrateFromSchema1(EngineMode saved) => saved switch
    {
        EngineMode.FolderWatcherDrills => EngineMode.ProxyDrills,
        EngineMode.FolderWatcherInfiniteTees => EngineMode.ProxyInfiniteTees,
        _ => saved,
    };

    public static void SaveLastMode(EngineMode mode)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                new { schema = CurrentSchema, lastMode = mode.ToString() },
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
