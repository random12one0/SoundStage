using System.Text.Json;
using Soundstage.Core.Abstractions;

namespace Soundstage.Core.State;

/// <summary>
/// Loads/saves the root application state as readable JSON. A corrupt state file is set
/// aside (never deleted) and replaced with defaults, so a bad write can't brick startup.
/// </summary>
public sealed class StateStore
{
    private readonly IFileSystem _fs;
    private readonly string _stateFilePath;

    public StateStore(IFileSystem fs, string stateFilePath)
    {
        _fs = fs;
        _stateFilePath = stateFilePath;
    }

    public string StateFilePath => _stateFilePath;

    public SoundstageState Load()
    {
        if (!_fs.FileExists(_stateFilePath))
        {
            return new SoundstageState();
        }

        try
        {
            var json = _fs.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<SoundstageState>(json, JsonDefaults.Readable) ?? new SoundstageState();
        }
        catch (JsonException)
        {
            QuarantineCorruptFile();
            return new SoundstageState();
        }
    }

    public void Save(SoundstageState state)
    {
        var json = JsonSerializer.Serialize(state, JsonDefaults.Readable);
        _fs.WriteAllTextAtomic(_stateFilePath, json);
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            _fs.CopyFile(_stateFilePath, _stateFilePath + ".corrupt", overwrite: true);
        }
        catch
        {
            // Quarantine is best-effort; defaults still load.
        }
    }
}
