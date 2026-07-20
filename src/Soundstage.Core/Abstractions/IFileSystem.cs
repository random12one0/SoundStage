namespace Soundstage.Core.Abstractions;

/// <summary>
/// Minimal file-system abstraction so all config-file logic is testable off-Windows.
/// Paths are treated as opaque strings; Windows-style paths flow through unchanged.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    string ReadAllText(string path);

    /// <summary>
    /// Writes the full content via a temp file in the same directory followed by an
    /// atomic rename, so a reader (Equalizer APO's file watcher) never sees a torn file.
    /// </summary>
    void WriteAllTextAtomic(string path, string content);

    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    void DeleteFile(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    /// <summary>Non-recursive listing of files in <paramref name="directory"/> matching a simple <c>*</c> pattern.</summary>
    IReadOnlyList<string> ListFiles(string directory, string searchPattern);
}

/// <summary>Real file system. Only exercised on Windows at runtime, but fully cross-platform.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tempPath, content, Utf8NoBom);
        try
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best effort cleanup; the original exception is what matters.
            }

            throw;
        }
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite) => File.Copy(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path) => File.Delete(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern) : [];
}
