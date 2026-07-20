using System.Text;
using System.Text.Json;
using Soundstage.Core.Abstractions;

namespace Soundstage.Core.Presets;

/// <summary>
/// The preset library: read-only built-ins (curated speaker presets from embedded
/// resources) plus user presets persisted as one JSON file each. Built-ins can be
/// duplicated but never modified or deleted — editing UX duplicates first.
/// </summary>
public sealed class PresetStore
{
    private readonly IFileSystem _fs;
    private readonly string _userPresetDirectory;
    private readonly IClock _clock;
    private readonly List<EqPreset> _builtIns;
    private readonly Dictionary<string, EqPreset> _byId = new(StringComparer.OrdinalIgnoreCase);

    public PresetStore(IFileSystem fs, string userPresetDirectory, IClock clock, IEnumerable<EqPreset>? builtIns = null)
    {
        _fs = fs;
        _userPresetDirectory = userPresetDirectory.TrimEnd('\\', '/');
        _clock = clock;
        _builtIns = (builtIns ?? EmbeddedPresets.LoadAll()).ToList();
        Reload();
    }

    public event Action? Changed;

    /// <summary>Built-ins first (curated order), then user presets sorted by name.</summary>
    public IReadOnlyList<EqPreset> All
    {
        get
        {
            var user = _byId.Values
                .Where(p => !p.IsBuiltIn)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
            return [.. _builtIns, .. user];
        }
    }

    public EqPreset? Get(string id) => _byId.GetValueOrDefault(id);

    public void Reload()
    {
        _byId.Clear();
        foreach (var preset in _builtIns)
        {
            preset.IsBuiltIn = true;
            _byId[preset.Id] = preset;
        }

        foreach (var path in _fs.ListFiles(_userPresetDirectory, "*.json"))
        {
            try
            {
                var preset = JsonSerializer.Deserialize<EqPreset>(_fs.ReadAllText(path), JsonDefaults.Readable);
                if (preset is not null && !string.IsNullOrWhiteSpace(preset.Id) && !_byId.ContainsKey(preset.Id))
                {
                    preset.IsBuiltIn = false;
                    _byId[preset.Id] = preset;
                }
            }
            catch (JsonException)
            {
                // A broken preset file is skipped, never deleted.
            }
        }
    }

    /// <summary>Saves a user preset (new or existing). Built-ins are immutable.</summary>
    public void Save(EqPreset preset)
    {
        if (preset.IsBuiltIn || (_byId.TryGetValue(preset.Id, out var existing) && existing.IsBuiltIn))
        {
            throw new InvalidOperationException($"Preset '{preset.Id}' is built-in; duplicate it instead.");
        }

        preset.ModifiedUtc = _clock.UtcNow;
        _fs.CreateDirectory(_userPresetDirectory);
        _fs.WriteAllTextAtomic(PathFor(preset.Id), JsonSerializer.Serialize(preset, JsonDefaults.Readable));
        _byId[preset.Id] = preset;
        Changed?.Invoke();
    }

    public EqPreset Duplicate(string id, string? newName = null)
    {
        var source = Get(id) ?? throw new KeyNotFoundException($"Preset '{id}' not found.");
        var name = newName ?? $"{source.Name} copy";
        var clone = source.Clone(UniqueIdFrom(name), name);
        clone.Category = source.IsBuiltIn ? PresetCategories.Custom : source.Category;
        Save(clone);
        return clone;
    }

    public void Rename(string id, string newName)
    {
        var preset = Get(id) ?? throw new KeyNotFoundException($"Preset '{id}' not found.");
        if (preset.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in presets cannot be renamed; duplicate instead.");
        }

        preset.Name = newName;
        Save(preset);
    }

    public void Delete(string id)
    {
        var preset = Get(id) ?? throw new KeyNotFoundException($"Preset '{id}' not found.");
        if (preset.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in presets cannot be deleted.");
        }

        _fs.DeleteFile(PathFor(id));
        _byId.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>Imports a preset under a unique id derived from its name.</summary>
    public EqPreset Import(EqPreset preset)
    {
        preset.Id = UniqueIdFrom(preset.Name);
        preset.IsBuiltIn = false;
        Save(preset);
        return preset;
    }

    public string UniqueIdFrom(string name)
    {
        var baseId = Slugify(name);
        if (!_byId.ContainsKey(baseId))
        {
            return baseId;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}-{i}";
            if (!_byId.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    public static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        var lastDash = true;
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "preset" : slug;
    }

    private string PathFor(string id) => $"{_userPresetDirectory}\\{id}.json";
}
