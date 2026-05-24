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

    /// <summary>Returns the port Infinite Tees is configured to listen on, or null if not found.</summary>
    public static int? GetInfiniteTeesPort()
    {
        if (!File.Exists(InfiniteTeesIniPath)) return null;
        var content = File.ReadAllText(InfiniteTeesIniPath);
        var match = PortRegex.Match(content);
        return match.Success && int.TryParse(match.Groups[1].Value, out var p) ? p : null;
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
}
