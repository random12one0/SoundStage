using Soundstage.Core.Abstractions;

namespace Soundstage.Core.Tests.Support;

/// <summary>
/// In-memory <see cref="IFileSystem"/> for tests. Case-insensitive, treats <c>\</c> and <c>/</c>
/// as equivalent, and records every write for assertions about write behavior.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public List<string> WriteLog { get; } = [];

    public IReadOnlyDictionary<string, string> Files => _files;

    private static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');

    public bool FileExists(string path) => _files.ContainsKey(Norm(path));

    public string ReadAllText(string path) =>
        _files.TryGetValue(Norm(path), out var content)
            ? content
            : throw new FileNotFoundException("Not found in InMemoryFileSystem", path);

    public void WriteAllTextAtomic(string path, string content)
    {
        var norm = Norm(path);
        _files[norm] = content;
        WriteLog.Add(norm);
        var dir = ParentOf(norm);
        if (dir.Length > 0)
        {
            _directories.Add(dir);
        }
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var source = Norm(sourcePath);
        var dest = Norm(destinationPath);
        if (!overwrite && _files.ContainsKey(dest))
        {
            throw new IOException($"Destination exists: {destinationPath}");
        }

        _files[dest] = ReadAllText(source);
        WriteLog.Add(dest);
    }

    public void DeleteFile(string path) => _files.Remove(Norm(path));

    public bool DirectoryExists(string path)
    {
        var norm = Norm(path);
        return _directories.Contains(norm) || _files.Keys.Any(f => f.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public void CreateDirectory(string path) => _directories.Add(Norm(path));

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern)
    {
        var norm = Norm(directory);
        var files = _files.Keys
            .Where(f => string.Equals(ParentOf(f), norm, StringComparison.OrdinalIgnoreCase))
            .Where(f => MatchesPattern(NameOf(f), searchPattern))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return files;
    }

    private static string ParentOf(string normPath)
    {
        var idx = normPath.LastIndexOf('\\');
        return idx < 0 ? string.Empty : normPath[..idx];
    }

    private static string NameOf(string normPath)
    {
        var idx = normPath.LastIndexOf('\\');
        return idx < 0 ? normPath : normPath[(idx + 1)..];
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern is "*" or "*.*")
        {
            return true;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.EndsWith("*", StringComparison.Ordinal))
        {
            return name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
