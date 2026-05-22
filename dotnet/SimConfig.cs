using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VxProxy;

/// <summary>
/// Read/write helpers for the target sim config files we touch.
/// </summary>
public static class SimConfig
{
    public static readonly string InfiniteTeesIniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InfiniteTees", "Saved", "Config", "Windows", "GameUserSettings.ini");

    public const string DrillsJsonPath =
        @"C:\Program Files\DrillsGolfSimulator\Drills\DrillsConnect\settings.json";

    private static readonly Regex PortRegex = new(@"Port=(\d+)", RegexOptions.Compiled);

    /// <summary>Returns the port iTees is configured to listen on, or null if not found.</summary>
    public static int? GetInfiniteTeesPort()
    {
        if (!File.Exists(InfiniteTeesIniPath)) return null;
        var content = File.ReadAllText(InfiniteTeesIniPath);
        var match = PortRegex.Match(content);
        return match.Success && int.TryParse(match.Groups[1].Value, out var p) ? p : null;
    }

    /// <summary>Rewrites iTees's listening port. Returns true if the file was changed.</summary>
    public static bool SetInfiniteTeesPort(int newPort)
    {
        if (!File.Exists(InfiniteTeesIniPath)) return false;
        var content = File.ReadAllText(InfiniteTeesIniPath);
        var updated = PortRegex.Replace(content, $"Port={newPort}");
        if (updated == content) return false;
        File.WriteAllText(InfiniteTeesIniPath, updated);
        return true;
    }

    /// <summary>Returns the port Drills is configured for, or null if not found / unreadable.</summary>
    public static int? GetDrillsPort()
    {
        if (!File.Exists(DrillsJsonPath)) return null;
        try
        {
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            using var doc = JsonDocument.Parse(File.ReadAllBytes(DrillsJsonPath), options);
            if (doc.RootElement.TryGetProperty("openConnect", out var oc)
                && oc.TryGetProperty("port", out var port)
                && port.ValueKind == JsonValueKind.Number)
            {
                return port.GetInt32();
            }
        }
        catch
        {
            // Treat malformed config as not configured.
        }
        return null;
    }

    /// <summary>Detects every sim configured for the proxy-bypass port 921 (may return a combined flags value).</summary>
    public static SimTarget DetectDirectTarget()
    {
        var result = SimTarget.None;
        if (GetDrillsPort() == 921) result |= SimTarget.Drills;
        if (GetInfiniteTeesPort() == 921) result |= SimTarget.InfiniteTees;
        return result;
    }

    /// <summary>
    /// Finds where folder-watcher-mode shots should be forwarded.
    /// Prefers an explicit direct-mode sim on 921; otherwise falls back to
    /// iTees's configured port (999 by default) so the standard setup still works.
    /// </summary>
    public static (SimTarget sim, int port) GetForwardTarget()
    {
        // Direct-mode sims listen on 921 — talk to them directly.
        // If both are configured, only one can actually bind 921 at a time, but
        // we just send to localhost:921 either way.
        if (GetDrillsPort() == 921) return (SimTarget.Drills, 921);
        if (GetInfiniteTeesPort() == 921) return (SimTarget.InfiniteTees, 921);

        // Default: iTees on its standard 999 port.
        var iteesPort = GetInfiniteTeesPort();
        if (iteesPort.HasValue) return (SimTarget.InfiniteTees, iteesPort.Value);

        // No sim config readable — assume iTees default.
        return (SimTarget.InfiniteTees, 999);
    }
}
