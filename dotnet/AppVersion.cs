using System.Reflection;

namespace VxProxy;

/// <summary>
/// The running build's version, for the window title, tray menu and log banner.
///
/// Reads InformationalVersion — set from &lt;InformationalVersion&gt; in the csproj, which
/// mirrors the release tag — and trims any '+' build metadata, so if
/// IncludeSourceRevisionInInformationalVersion is ever turned back on a commit hash
/// doesn't leak into the UI. Falls back to the assembly version if the attribute is
/// missing (it won't be in a normal build, but single-file publish trimming has
/// surprised us before).
/// </summary>
public static class AppVersion
{
    /// <summary>Version without the leading 'v', e.g. "1.4.1".</summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(AppVersion).Assembly;

        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return asm.GetName().Version?.ToString(3) ?? "unknown";
    }
}
