using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Soundstage.Core.Abstractions;
using Soundstage.Core.Effects;

namespace Soundstage.App.Services;

/// <summary>
/// Locates and installs the VST rack DLLs. The rack ships embedded in the app and self-extracts to
/// the per-user plugin folder on first use, so the enhancers work out of the box with no download;
/// the online bundle and folder-import paths remain as fallbacks. The compiler skips any effect whose
/// DLL can't be resolved, so a missing plugin never breaks audio. All rack plugins are Airwindows
/// (MIT), so we're free to embed and ship them.
/// </summary>
public sealed class VstPluginService
{
    /// <summary>Airwindows 64-bit VST2 bundle (the whole pack; we extract just the rack's DLLs). MIT.</summary>
    public const string BundleUrl = "https://www.airwindows.com/wp-content/uploads/WinVST64s.zip";

    public const string DownloadPageUrl = "https://www.airwindows.com/vsts/";

    /// <summary>Manifest-resource prefix for the embedded rack DLLs (see the .csproj LogicalName).</summary>
    private const string EmbeddedResourcePrefix = "Soundstage.Plugins.";

    private readonly string _bundledDir = Path.Combine(AppContext.BaseDirectory, "plugins");
    private readonly object _extractLock = new();

    /// <summary>Where downloaded plugins land (the app data folder, always writable).</summary>
    public string UserPluginDirectory { get; } = Path.Combine(DefaultAppPaths.DataDirectory, "plugins");

    /// <summary>Absolute path to a plugin DLL if it's installed (bundled or downloaded), else null.</summary>
    public string? Resolve(string dllFileName)
    {
        // 1) A loose "plugins" folder next to the app (dev builds / manual drop-in).
        var bundled = Path.Combine(_bundledDir, dllFileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // 2) The stable per-user plugin folder (self-extracted, downloaded, or imported).
        var user = Path.Combine(UserPluginDirectory, dllFileName);
        if (File.Exists(user))
        {
            return user;
        }

        // 3) Self-extract the copy shipped inside the app to the user folder. This is what makes the
        //    rack work out of the box for the single-file portable build, with a path APO can load.
        var extracted = TryExtractEmbedded(dllFileName);
        if (extracted is not null)
        {
            return extracted;
        }

        // 4) Lenient scan: a file whose canonical key matches (Airwindows "64" suffix, odd casing,
        //    spaces, "(1)" copies) — for packs the user dropped in themselves.
        return FindLenient(_bundledDir, dllFileName) ?? FindLenient(UserPluginDirectory, dllFileName);
    }

    /// <summary>
    /// Writes the app-embedded copy of <paramref name="dllFileName"/> into the per-user plugin folder
    /// (once, refreshing only when the shipped bytes differ) and returns its path, or null if the app
    /// doesn't embed it. Never throws — falls back to any copy already on disk.
    /// </summary>
    private string? TryExtractEmbedded(string dllFileName)
    {
        var name = Path.GetFileName(dllFileName);
        var target = Path.Combine(UserPluginDirectory, name);
        var asm = typeof(VstPluginService).Assembly;

        lock (_extractLock)
        {
            try
            {
                using var res = asm.GetManifestResourceStream(EmbeddedResourcePrefix + name);
                if (res is null)
                {
                    return File.Exists(target) ? target : null;
                }

                if (File.Exists(target) && new FileInfo(target).Length == res.Length)
                {
                    return target;
                }

                Directory.CreateDirectory(UserPluginDirectory);
                var tmp = Path.Combine(UserPluginDirectory, name + ".tmp");
                using (var file = File.Create(tmp))
                {
                    res.CopyTo(file);
                }

                File.Move(tmp, target, overwrite: true);
                return target;
            }
            catch
            {
                // A locked/unwritable target (e.g. APO has it open) — an existing copy still works.
                return File.Exists(target) ? target : null;
            }
        }
    }

    private static string? FindLenient(string dir, string dllFileName)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
            {
                if (VstNaming.Matches(dllFileName, Path.GetFileName(file)))
                {
                    return file;
                }
            }
        }
        catch
        {
            // Unreadable folder — treat as not found.
        }

        return null;
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
            // Match the pack's entries to the catalog by canonical key, then save each under our
            // catalog filename so the exact-path resolver finds it afterwards.
            var wanted = BuildWantedByKey();
            var saved = new HashSet<string>(StringComparer.Ordinal);
            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var key = VstNaming.NormalizeKey(name);
                    if (wanted.TryGetValue(key, out var canonical) && saved.Add(key))
                    {
                        entry.ExtractToFile(Path.Combine(UserPluginDirectory, canonical), overwrite: true);
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

    /// <summary>
    /// Copies the rack's DLLs out of a folder the user already downloaded (searched recursively,
    /// case-insensitive) into the plugin folder. Returns how many were found. This is the "I already
    /// have the pack — just wire them in" path, so there's no re-download.
    /// </summary>
    public int ImportFromFolder(string sourceDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            return 0;
        }

        Directory.CreateDirectory(UserPluginDirectory);
        var wanted = BuildWantedByKey();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sourceDir, "*.dll", SearchOption.AllDirectories);
        }
        catch
        {
            return 0;
        }

        var copied = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var key = VstNaming.NormalizeKey(Path.GetFileName(file));
            if (wanted.TryGetValue(key, out var canonical) && seen.Add(key))
            {
                try
                {
                    File.Copy(file, Path.Combine(UserPluginDirectory, canonical), overwrite: true);
                    copied++;
                }
                catch
                {
                    // Skip a locked/unreadable file; the rest still import.
                }
            }
        }

        return copied;
    }

    /// <summary>Catalog DLLs indexed by their canonical key, mapping to the filename we save under.</summary>
    private static Dictionary<string, string> BuildWantedByKey()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var effect in VstCatalog.All)
        {
            map[VstNaming.NormalizeKey(effect.DllFileName)] = effect.DllFileName;
        }

        return map;
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
