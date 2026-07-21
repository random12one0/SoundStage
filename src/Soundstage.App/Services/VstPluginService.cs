using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Soundstage.Core.Abstractions;
using Soundstage.Core.Effects;

namespace Soundstage.App.Services;

/// <summary>
/// Locates and installs the bundled VST rack DLLs. Effects resolve to a DLL either shipped next to
/// the app (installer-bundled) or downloaded into the per-user data folder; the compiler skips any
/// effect whose DLL isn't present, so a missing rack never breaks audio. All rack plugins are
/// Airwindows (MIT), so we're free to fetch and ship them.
/// </summary>
public sealed class VstPluginService
{
    /// <summary>Airwindows 64-bit VST2 bundle (the whole pack; we extract just the rack's DLLs). MIT.</summary>
    public const string BundleUrl = "https://www.airwindows.com/wp-content/uploads/WinVST64s.zip";

    public const string DownloadPageUrl = "https://www.airwindows.com/vsts/";

    private readonly string _bundledDir = Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Where downloaded plugins land (the app data folder, always writable).</summary>
    public string UserPluginDirectory { get; } = Path.Combine(DefaultAppPaths.DataDirectory, "plugins");

    /// <summary>Absolute path to a plugin DLL if it's installed (bundled or downloaded), else null.</summary>
    public string? Resolve(string dllFileName)
    {
        var bundled = Path.Combine(_bundledDir, dllFileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var user = Path.Combine(UserPluginDirectory, dllFileName);
        return File.Exists(user) ? user : null;
    }

    public int InstalledCount => VstCatalog.All.Count(e => Resolve(e.DllFileName) is not null);

    public int TotalCount => VstCatalog.All.Count;

    public bool AllInstalled => InstalledCount == TotalCount;

    public bool CanAutoInstall => !string.IsNullOrEmpty(BundleUrl);

    /// <summary>
    /// Downloads the Airwindows bundle and extracts just the rack's DLLs into the user plugin folder.
    /// Returns true when every catalog plugin is present afterwards. Never throws — reports false on
    /// any failure so the UI can fall back to a manual install.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (!CanAutoInstall)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(UserPluginDirectory);
            var tmpZip = Path.Combine(Path.GetTempPath(), "soundstage-airwindows-vst2.zip");

            status?.Report("Downloading the effect pack…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            await using (var stream = await http.GetStreamAsync(BundleUrl, ct))
            await using (var file = File.Create(tmpZip))
            {
                await stream.CopyToAsync(file, ct);
            }

            status?.Report("Installing effects…");
            var wanted = VstCatalog.All.Select(e => e.DllFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (!string.IsNullOrEmpty(name) && wanted.Contains(name))
                    {
                        entry.ExtractToFile(Path.Combine(UserPluginDirectory, name), overwrite: true);
                    }
                }
            }

            TryDelete(tmpZip);
            return AllInstalled;
        }
        catch
        {
            return false;
        }
    }

    public void OpenPluginFolder()
    {
        try
        {
            Directory.CreateDirectory(UserPluginDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", UserPluginDirectory) { UseShellExecute = true });
        }
        catch
        {
            // Non-critical.
        }
    }

    public void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true });
        }
        catch
        {
            // Non-critical.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Ignore.
        }
    }
}
