using System.Globalization;

namespace Soundstage.Core.Update;

/// <summary>Version parsing and comparison for update checks.</summary>
public static class UpdatePolicy
{
    /// <summary>
    /// Parses a release tag like <c>v0.4.0</c> or <c>0.4.0</c> into a version. Returns false
    /// for anything unparseable (pre-release suffixes like <c>-beta</c> are trimmed).
    /// </summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var cleaned = tag.Trim();
        if (cleaned.StartsWith('v') || cleaned.StartsWith('V'))
        {
            cleaned = cleaned[1..];
        }

        // Drop a pre-release/build suffix: "0.4.0-beta.1" → "0.4.0".
        var dash = cleaned.IndexOfAny(['-', '+']);
        if (dash >= 0)
        {
            cleaned = cleaned[..dash];
        }

        // Version.Parse needs at least major.minor; pad a bare "1" to "1.0".
        if (!cleaned.Contains('.'))
        {
            cleaned += ".0";
        }

        return Version.TryParse(cleaned, out version!);
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a newer release than <paramref name="current"/>,
    /// comparing only major.minor.build (revision/assembly 4th part is ignored).
    /// </summary>
    public static bool IsNewer(Version current, Version latest) => Normalize(latest) > Normalize(current);

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    /// <summary>The running application version (major.minor.build), for display and comparison.</summary>
    public static string Format(Version v) => Normalize(v).ToString(3);

    /// <summary>Formats a byte count as a friendly size, e.g. "70.2 MB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "";
        }

        double mb = bytes / 1024.0 / 1024.0;
        return mb >= 1
            ? mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
    }
}
