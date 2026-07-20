using System.IO;

namespace Soundstage.App.Services;

/// <summary>
/// Finds already-installed 64-bit VST 2 compressor DLLs in the standard locations (plus
/// the Downloads folder, since LoudMax ships as a bare DLL). Licensing keeps us from
/// bundling a compressor; this makes "one download" the only manual step.
/// </summary>
public static class VstScanner
{
    /// <summary>Known free compressor/limiter DLL name fragments, in preference order.</summary>
    private static readonly string[] KnownNames =
        ["loudmax", "kotelnikov", "roughrider", "rough rider", "roughrider3", "mcompressor", "tdr", "molot", "compressor", "limiter"];

    /// <summary>Best single match, or null if none found. 64-bit builds are strongly preferred.</summary>
    public static string? FindBestCompressor() => FindCompressors().FirstOrDefault();

    public static IReadOnlyList<string> FindCompressors()
    {
        var results = new List<(string Path, int NameRank, int Bitness)>();

        foreach (var (dir, depth) in CandidateDirectories())
        {
            EnumerateDlls(dir, depth, results);
        }

        return results
            .OrderBy(r => r.NameRank)
            .ThenBy(r => r.Bitness)
            .ThenBy(r => r.Path.Length)
            .Select(r => r.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void EnumerateDlls(string directory, int maxDepth, List<(string, int, int)> results)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var rank = Array.FindIndex(KnownNames, k => name.Contains(k));
                if (rank < 0)
                {
                    continue;
                }

                // Equalizer APO is 64-bit: strongly prefer a 64-marked DLL, then unmarked,
                // and rank 32-bit builds last (they won't load).
                var pathLower = file.ToLowerInvariant();
                var is64 = name.Contains("64") || pathLower.Contains("64-bit") || pathLower.Contains("win64") || pathLower.Contains("x64");
                var is32 = name.Contains("32") || name.Contains("x86") || pathLower.Contains("32-bit") || pathLower.Contains("win32");
                var bitness = is64 ? 0 : is32 ? 2 : 1;
                results.Add((file, rank, bitness));
            }

            if (maxDepth > 0)
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    EnumerateDlls(sub, maxDepth - 1, results);
                }
            }
        }
        catch
        {
            // Access denied / IO problems on a candidate folder — just move on.
        }
    }

    private static IEnumerable<(string Path, int Depth)> CandidateDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var commonProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return (Path.Combine(programFiles, "VSTPlugins"), 3);
        yield return (Path.Combine(programFiles, "Steinberg", "VSTPlugins"), 3);
        yield return (Path.Combine(programFiles, "Common Files", "VST2"), 3);
        yield return (Path.Combine(commonProgramFiles, "VST2"), 3);
        yield return (Path.Combine(commonProgramFiles, "VST3"), 3);
        yield return (Path.Combine(commonProgramFiles, "Steinberg", "VST2"), 3);
        // People drop a bare LoudMax DLL straight into Downloads/Desktop, often inside its zip folder.
        yield return (Path.Combine(userProfile, "Downloads"), 3);
        yield return (Path.Combine(userProfile, "Desktop"), 2);
        yield return (Path.Combine(userProfile, "Documents"), 2);
    }
}
