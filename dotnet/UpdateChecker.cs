using System.Net.Http;
using System.Text.Json;

namespace VxProxy;

/// <summary>A newer release than the one we're running, with its download URLs resolved.</summary>
public sealed record AvailableUpdate(
    Version Version,
    string Tag,
    string ExeUrl,
    string Sha256Url,
    string ReleaseUrl,
    long SizeBytes)
{
    /// <summary>e.g. "78.4 MB" — for the confirm dialog, since this is a big download.</summary>
    public string SizeLabel => SizeBytes > 0
        ? $"{SizeBytes / 1024.0 / 1024.0:F0} MB"
        : "unknown size";
}

public enum CheckOutcome { UpToDate, UpdateAvailable, Failed }

public sealed record CheckResult(CheckOutcome Outcome, AvailableUpdate? Update);

/// <summary>
/// Polls the GitHub Releases API for a newer VX Connector and raises
/// <see cref="UpdateFound"/> when it sees one. Never downloads or installs anything —
/// that's <see cref="UpdateApplier"/>, and only on an explicit user click.
///
/// This is the only outbound internet traffic the app makes; everything else is loopback
/// TCP to the sim. It can be turned off entirely from the tray menu (persisted as
/// "checkForUpdates" in <see cref="UserSettings"/>).
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/zonkey-acoustic/vx-connector/releases/latest";

    // Asset names are stable across every release so far, and the release workflow
    // hardcodes them too. Resolved out of the API response rather than by building a
    // download URL by hand, so a renamed asset surfaces as "asset missing" instead of a 404.
    private const string ExeAssetName = "vx-connector.exe";
    private const string Sha256AssetName = "vx-connector.exe.sha256";

    /// <summary>
    /// Don't check the instant we launch: at login the network stack often isn't up yet,
    /// and a failed first check would just be noise in the log.
    /// </summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = GitHubHttp.Create(TimeSpan.FromSeconds(15));

    private string? _lastFailure;
    private Version? _announced;

    /// <summary>Raised once per newly-discovered version, from a background thread.</summary>
    public event Action<AvailableUpdate>? UpdateFound;

    /// <summary>The newest update we know about, or null if we're current.</summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>Mirrors the persisted preference. A manual check ignores this.</summary>
    public bool AutoCheckEnabled { get; set; } = true;

    public UpdateChecker(Action<string> log) => _log = log;

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, ct);

            while (!ct.IsCancellationRequested)
            {
                if (AutoCheckEnabled) await CheckAsync(ct);
                await Task.Delay(CheckInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Check on demand, from the tray menu. Clears the announce latch so an update we
    /// already know about still gets surfaced — the user asked, so they get an answer.
    /// </summary>
    public Task<CheckResult> CheckNowAsync(CancellationToken ct = default)
    {
        _announced = null;
        _lastFailure = null;
        return CheckAsync(ct);
    }

    private async Task<CheckResult> CheckAsync(CancellationToken ct)
    {
        if (!Version.TryParse(AppVersion.Display, out var running))
        {
            // Only possible if the version attributes went missing from the build.
            return Fail($"can't parse our own version ({AppVersion.Display})");
        }

        try
        {
            using var response = await _http.GetAsync(
                LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return Fail($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return Fail("latest release has no tag");
            if (!TryParseTag(tag, out var latest)) return Fail($"unrecognised release tag \"{tag}\"");

            if (latest <= Normalize(running))
            {
                _lastFailure = null;
                Available = null;
                return new CheckResult(CheckOutcome.UpToDate, null);
            }

            var (exeUrl, size) = FindAsset(root, ExeAssetName);
            var (shaUrl, _) = FindAsset(root, Sha256AssetName);
            if (exeUrl is null || shaUrl is null)
                return Fail($"release {tag} is missing {ExeAssetName} or {Sha256AssetName}");

            var releaseUrl = root.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? LatestReleaseUrl
                : LatestReleaseUrl;

            var update = new AvailableUpdate(latest, tag, exeUrl, shaUrl, releaseUrl, size);
            Available = update;
            _lastFailure = null;

            if (_announced != latest)
            {
                _announced = latest;
                _log($"Update available: v{latest} ({update.SizeLabel}). " +
                     $"You're on {AppVersion.Display} — install it from the tray menu.");
                UpdateFound?.Invoke(update);
            }

            return new CheckResult(CheckOutcome.UpdateAvailable, update);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, TLS interception, rate limited, timeout — all the
            // same to us: say it once, stay quiet until something changes.
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Report a failed check at most once per distinct reason. The retry cadence is every
    /// 6h, but a laptop that's offline all weekend shouldn't fill the log with the same
    /// line — same reasoning as ProxyEngine's connect-failure dedupe.
    /// </summary>
    private CheckResult Fail(string reason)
    {
        if (_lastFailure != reason)
        {
            _lastFailure = reason;
            _log($"Could not check for updates: {reason}");
        }
        return new CheckResult(CheckOutcome.Failed, null);
    }

    private static (string? Url, long Size) FindAsset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, 0);

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n) || n.GetString() != name) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64()
                : 0;
            return (url, size);
        }

        return (null, 0);
    }

    /// <summary>Tags are "v1.4.2"; the leading v is ours, not part of the version.</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        var trimmed = tag.TrimStart('v', 'V');
        if (Version.TryParse(trimmed, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>
    /// Flatten to three parts. Version.Parse("1.4.2") leaves Revision at -1 while
    /// "1.4.2.0" sets it to 0, and those two compare as unequal — which would make an
    /// identical version look like an update.
    /// </summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}

/// <summary>
/// Shared HttpClient construction. GitHub rejects API requests that arrive without a
/// User-Agent, and both the version check and the binary download need one.
/// </summary>
internal static class GitHubHttp
{
    public static HttpClient Create(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"vx-connector/{AppVersion.Display}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
