namespace Soundstage.Core.Update;

/// <summary>A downloadable file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes, string? Sha256);

/// <summary>Everything the updater needs about the latest published release.</summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string Name,
    string Notes,
    string HtmlUrl,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The Inno installer asset (preferred for updating), if present.</summary>
    public ReleaseAsset? Installer =>
        Assets.FirstOrDefault(a => a.Name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
        ?? Assets.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The portable single-file EXE asset, if present.</summary>
    public ReleaseAsset? Portable =>
        Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));

    /// <summary>The best asset to download for an update: installer first, else portable.</summary>
    public ReleaseAsset? PreferredAsset => Installer ?? Portable;
}

/// <summary>Result of comparing the running build to the latest release.</summary>
public sealed record UpdateCheckResult(Version CurrentVersion, UpdateInfo? Latest, bool IsUpdateAvailable)
{
    public static UpdateCheckResult UpToDate(Version current, UpdateInfo? latest) => new(current, latest, false);

    public static UpdateCheckResult Available(Version current, UpdateInfo latest) => new(current, latest, true);
}
