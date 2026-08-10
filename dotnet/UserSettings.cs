using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VxProxy;

/// <summary>
/// Persists VX Connector's own preferences: the last-used mode, and whether to check
/// GitHub for new releases. Read on startup; written when the user changes either from
/// the tray menu. CLI flags are intentionally transient and never touch this file —
/// they're one-shot overrides.
///
/// Every write is a read-modify-write of the whole file. There is more than one key
/// now, and each setter has to preserve the others rather than serialize its own value
/// into a fresh document.
///
/// Note "Start with Windows" is deliberately NOT stored here — see
/// <see cref="StartupRegistration"/>: the registry is its only source of truth.
/// </summary>
public static class UserSettings
{
    /// <summary>
    /// Bumped when a saved value needs reinterpreting. Files written before this
    /// existed (v1.3.3 and earlier) have no "schema" key and are treated as 1.
    ///
    /// Adding a key doesn't need a bump — an absent key falls back to its default.
    /// </summary>
    private const int CurrentSchema = 2;

    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VxConnector", "settings.json");

    /// <summary>What's on disk. Null members mean "the file didn't say".</summary>
    private sealed record Stored(EngineMode? LastMode, bool? UpdateChecks, int Schema);

    public static EngineMode? LoadLastMode()
    {
        var stored = Read();
        if (stored.LastMode is not { } mode) return null;

        if (stored.Schema < CurrentSchema)
        {
            mode = MigrateFromSchema1(mode);
            Write(mode, stored.UpdateChecks);
        }

        return mode;
    }

    public static void SaveLastMode(EngineMode mode) => Write(mode, Read().UpdateChecks);

    /// <summary>Background update checks are on unless the user turned them off.</summary>
    public static bool LoadUpdateChecksEnabled() => Read().UpdateChecks ?? true;

    public static void SaveUpdateChecksEnabled(bool enabled) => Write(Read().LastMode, enabled);

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

    private static Stored Read()
    {
        try
        {
            // No file yet: nothing saved, and nothing to migrate either.
            if (!File.Exists(SettingsPath)) return new Stored(null, null, CurrentSchema);

            using var doc = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
            var root = doc.RootElement;

            EngineMode? mode =
                root.TryGetProperty("lastMode", out var m)
                && m.ValueKind == JsonValueKind.String
                && Enum.TryParse<EngineMode>(m.GetString(), out var parsed)
                    ? parsed
                    : null;

            bool? checks =
                root.TryGetProperty("checkForUpdates", out var c)
                && c.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? c.GetBoolean()
                    : null;

            int schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32()
                : 1;

            return new Stored(mode, checks, schema);
        }
        catch
        {
            // Locked or malformed — treat as no saved preferences.
        }
        return new Stored(null, null, CurrentSchema);
    }

    private static void Write(EngineMode? lastMode, bool? updateChecks)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(
                new
                {
                    schema = CurrentSchema,
                    lastMode = lastMode?.ToString(),
                    checkForUpdates = updateChecks,
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    // Keep unset preferences out of the file rather than writing nulls,
                    // so an absent key keeps meaning "use the default".
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                });

            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort: writes that fail (e.g. permission denied) shouldn't
            // crash the app — the user just won't get sticky behavior.
        }
    }
}
