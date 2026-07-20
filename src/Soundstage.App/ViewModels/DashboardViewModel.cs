using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Soundstage.App.Services;
using Soundstage.Core.Apo;
using Soundstage.Core.Dsp;
using Soundstage.Core.Import;
using Soundstage.Core.Presets;

namespace Soundstage.App.ViewModels;

/// <summary>One editable parametric band row.</summary>
public partial class BandViewModel : ObservableObject
{
    private readonly Action _changed;

    public BandViewModel(EqBand band, Action changed)
    {
        _changed = changed;
        _type = band.Type;
        _frequencyHz = band.FrequencyHz;
        _gainDb = band.GainDb;
        _q = band.Q;
        _enabled = band.Enabled;
    }

    public static IReadOnlyList<FilterType> FilterTypes { get; } = Enum.GetValues<FilterType>();

    [ObservableProperty]
    private FilterType _type;

    [ObservableProperty]
    private double _frequencyHz;

    [ObservableProperty]
    private double _gainDb;

    [ObservableProperty]
    private double _q;

    [ObservableProperty]
    private bool _enabled;

    partial void OnTypeChanged(FilterType value) => _changed();

    partial void OnFrequencyHzChanged(double value) => _changed();

    partial void OnGainDbChanged(double value) => _changed();

    partial void OnQChanged(double value) => _changed();

    partial void OnEnabledChanged(bool value) => _changed();

    public EqBand ToBand() => new(Type, Math.Clamp(FrequencyHz, 10, 24000), Math.Clamp(GainDb, -30, 20), Math.Clamp(Q, 0.05, 20), Enabled);
}

/// <summary>One graphic-EQ slider.</summary>
public partial class GraphicBandViewModel : ObservableObject
{
    private readonly Action<int, double> _changed;

    public GraphicBandViewModel(int index, double frequencyHz, double value, Action<int, double> changed)
    {
        Index = index;
        _changed = changed;
        FrequencyLabel = frequencyHz >= 1000 ? $"{frequencyHz / 1000:0.#}k" : $"{frequencyHz:0}";
        _value = value;
    }

    public int Index { get; }

    public string FrequencyLabel { get; }

    [ObservableProperty]
    private double _value;

    partial void OnValueChanged(double value) => _changed(Index, value);
}

/// <summary>
/// The main page: preset picking (instant apply), A/B bypass, headroom, EQ editing, and
/// AutoEq headphone search. Built-ins are duplicated on first edit.
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _syncing;
    private IDisposable? _editDebounce;

    [ObservableProperty]
    private ObservableCollection<EqPreset> _presets = [];

    [ObservableProperty]
    private EqPreset? _selectedPreset;

    [ObservableProperty]
    private bool _isBypassed;

    [ObservableProperty]
    private string _headroomSummary = "";

    [ObservableProperty]
    private string _safetyNote = "";

    [ObservableProperty]
    private bool _isEditable;

    [ObservableProperty]
    private ObservableCollection<BandViewModel> _bands = [];

    [ObservableProperty]
    private ObservableCollection<GraphicBandViewModel> _graphicBands = [];

    [ObservableProperty]
    private bool _isParametric = true;

    [ObservableProperty]
    private IReadOnlyList<Point> _curvePoints = [];

    [ObservableProperty]
    private string _autoEqQuery = "";

    [ObservableProperty]
    private ObservableCollection<AutoEqEntry> _autoEqResults = [];

    [ObservableProperty]
    private string _importStatus = "";

    public bool ApoAvailable => _services.ApoAvailable;

    public int AutoEqModelCount => _services.AutoEq.Count;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(SyncFromState);
            controller.Applied += result => UiDispatch.Post(() => OnApplied(result));
        }

        services.Presets.Changed += () => UiDispatch.Post(RefreshPresets);
        RefreshPresets();
        SyncFromState();
    }

    // ---- Preset picking ----

    private void RefreshPresets()
    {
        var selectedId = SelectedPreset?.Id;
        _syncing = true;
        Presets = new ObservableCollection<EqPreset>(_services.Presets.All);
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == (selectedId ?? _services.Controller?.ActiveProfile?.ActivePresetId));
        _syncing = false;

        // Rebuild the editor only when the selection actually changed — a refresh caused
        // by saving our own live edits must not yank band rows out from under a drag.
        if (SelectedPreset?.Id != selectedId)
        {
            RebuildEditor();
        }
    }

    private void SyncFromState()
    {
        var controller = _services.Controller;
        if (controller is null)
        {
            return;
        }

        _syncing = true;
        OnPropertyChanged(nameof(CanUndo));
        IsBypassed = controller.State.BypassActive;
        var activeId = controller.ActiveProfile?.ActivePresetId;
        if (SelectedPreset?.Id != activeId)
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == activeId);
            RebuildEditor();
        }

        _syncing = false;
    }

    partial void OnSelectedPresetChanged(EqPreset? value)
    {
        if (_syncing)
        {
            return;
        }

        if (value is not null)
        {
            _services.Controller?.ApplyPreset(value.Id);
        }

        RebuildEditor();
    }

    partial void OnIsBypassedChanged(bool value)
    {
        if (!_syncing)
        {
            _services.Controller?.SetBypass(value);
        }
    }

    [RelayCommand]
    private void ToggleBypass() => _services.Controller?.ToggleBypass();

    public bool CanUndo => _services.Controller?.CanUndo ?? false;

    [RelayCommand]
    private void Undo()
    {
        _services.Controller?.Undo();
        RefreshPresets();
        RebuildEditor();
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Marks the start of an editing gesture: one undo step per gesture, not per tick.</summary>
    private void PushUndoIfGestureStart()
    {
        if (_editDebounce is null)
        {
            _services.Controller?.PushUndoSnapshot();
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    [RelayCommand]
    private void ResetEq()
    {
        var preset = EnsureEditablePreset();
        if (preset is null)
        {
            return;
        }

        _services.Controller?.PushUndoSnapshot();
        foreach (var band in Bands)
        {
            band.GainDb = 0;
        }

        foreach (var graphicBand in GraphicBands)
        {
            graphicBand.Value = 0;
        }

        OnPropertyChanged(nameof(CanUndo));
    }

    // ---- Preset management ----

    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var copy = _services.Presets.Duplicate(SelectedPreset.Id);
        RefreshPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == copy.Id);
    }

    [RelayCommand]
    private void Rename()
    {
        if (SelectedPreset is null || SelectedPreset.IsBuiltIn)
        {
            return;
        }

        var newName = Views.InputDialog.Show("Rename preset", "New name:", SelectedPreset.Name);
        if (!string.IsNullOrWhiteSpace(newName))
        {
            _services.Presets.Rename(SelectedPreset.Id, newName.Trim());
            RefreshPresets();
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedPreset is null || SelectedPreset.IsBuiltIn)
        {
            return;
        }

        var doomed = SelectedPreset;
        var fallback = Presets.FirstOrDefault(p => p.Id == "flat") ?? Presets.FirstOrDefault(p => p.Id != doomed.Id);
        if (fallback is not null)
        {
            _services.Controller?.ApplyPreset(fallback.Id);
        }

        _services.Presets.Delete(doomed.Id);
        RefreshPresets();
    }

    [RelayCommand]
    private void ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import preset",
            Filter = "EQ presets (*.txt;*.peace)|*.txt;*.peace|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var text = System.IO.File.ReadAllText(dialog.FileName);
            var name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);

            EqPreset preset;
            IReadOnlyList<string> warnings;
            if (dialog.FileName.EndsWith(".peace", StringComparison.OrdinalIgnoreCase))
            {
                var outcome = PeaceImporter.Import(text, name);
                (preset, warnings) = (outcome.Preset, outcome.Warnings);
            }
            else
            {
                var outcome = AutoEqImporter.Import(text, name);
                (preset, warnings) = (outcome.Preset, outcome.Warnings);
                if (preset.Bands.Count == 0)
                {
                    // Not APO syntax — maybe a Peace INI export saved as .txt.
                    var peaceOutcome = PeaceImporter.Import(text, name);
                    (preset, warnings) = (peaceOutcome.Preset, peaceOutcome.Warnings);
                }
            }

            if (preset.Bands.Count == 0)
            {
                ImportStatus = "Nothing importable found in that file.";
                return;
            }

            var imported = _services.Presets.Import(preset);
            RefreshPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == imported.Id);
            ImportStatus = warnings.Count > 0
                ? $"Imported “{imported.Name}” with {warnings.Count} warning(s)."
                : $"Imported “{imported.Name}”.";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Import failed: {ex.Message}";
        }
    }

    // ---- AutoEq search ----

    partial void OnAutoEqQueryChanged(string value)
    {
        AutoEqResults = value.Trim().Length >= 2
            ? new ObservableCollection<AutoEqEntry>(_services.AutoEq.Search(value, 25))
            : [];
    }

    [RelayCommand]
    private void AddAutoEqPreset(AutoEqEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var imported = _services.Presets.Import(entry.ToPreset());
        RefreshPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == imported.Id);
        ImportStatus = $"Added “{entry.Model}” correction.";
    }

    // ---- EQ editing ----

    /// <summary>
    /// Editing a built-in preset transparently forks it into the user's own copy first —
    /// nobody should meet a locked dial. The editor keeps its live state; only the target
    /// of the debounced save changes.
    /// </summary>
    private EqPreset? EnsureEditablePreset()
    {
        var preset = SelectedPreset;
        if (preset is null)
        {
            return null;
        }

        if (!preset.IsBuiltIn)
        {
            return preset;
        }

        var copy = _services.Presets.Duplicate(preset.Id, $"{preset.Name} (custom)");
        _syncing = true;
        Presets = new ObservableCollection<EqPreset>(_services.Presets.All);
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == copy.Id);
        _syncing = false;
        _services.Controller?.ApplyPreset(copy.Id);
        IsEditable = true;
        ImportStatus = $"Created “{copy.Name}” — built-in presets stay untouched.";
        return copy;
    }

    private void RebuildEditor()
    {
        _editDebounce?.Dispose();
        var preset = SelectedPreset;
        IsEditable = preset is not null && !preset.IsBuiltIn;
        IsParametric = preset?.Mode is null or EqMode.Parametric;

        Bands = preset is { Mode: EqMode.Parametric }
            ? new ObservableCollection<BandViewModel>(preset.Bands.Select(b => new BandViewModel(b, OnEditorChanged)))
            : [];

        if (preset is not null && preset.Mode != EqMode.Parametric)
        {
            var freqs = GraphicBands2(preset.Mode);
            var gains = preset.GraphicGains;
            GraphicBands = new ObservableCollection<GraphicBandViewModel>(
                freqs.Select((f, i) => new GraphicBandViewModel(i, f, i < gains.Length ? gains[i] : 0, OnGraphicChanged)));
        }
        else
        {
            GraphicBands = [];
        }

        RecomputeCurve();
    }

    private static double[] GraphicBands2(EqMode mode) => Core.Presets.GraphicBands.FrequenciesFor(mode);

    [RelayCommand]
    private void AddBand()
    {
        PushUndoIfGestureStart();
        if (EnsureEditablePreset() is null)
        {
            return;
        }

        Bands.Add(new BandViewModel(new EqBand(FilterType.Peaking, 1000, 0, 1.41), OnEditorChanged));
        OnEditorChanged();
    }

    [RelayCommand]
    private void RemoveBand(BandViewModel? band)
    {
        PushUndoIfGestureStart();
        if (band is null || EnsureEditablePreset() is null)
        {
            return;
        }

        Bands.Remove(band);
        OnEditorChanged();
    }

    [RelayCommand]
    private void SetMode(string? modeName)
    {
        if (!Enum.TryParse<EqMode>(modeName, out var mode) || SelectedPreset?.Mode == mode)
        {
            return;
        }

        var preset = EnsureEditablePreset();
        if (preset is null)
        {
            return;
        }

        preset.Mode = mode;
        if (mode != EqMode.Parametric)
        {
            var length = Core.Presets.GraphicBands.FrequenciesFor(mode).Length;
            if (preset.GraphicGains.Length != length)
            {
                preset.GraphicGains = new double[length];
            }
        }

        _services.Presets.Save(preset);
        RebuildEditor();
        _services.Controller?.NotifyPresetContentChanged();
    }

    private void OnGraphicChanged(int index, double value)
    {
        PushUndoIfGestureStart();
        var preset = EnsureEditablePreset();
        if (preset is null)
        {
            return;
        }

        if (index < preset.GraphicGains.Length)
        {
            preset.GraphicGains[index] = Math.Clamp(value, -15, 15);
        }

        OnEditorChanged();
    }

    /// <summary>Debounced: writes the working preset + reapplies 350 ms after the last tweak.</summary>
    private void OnEditorChanged()
    {
        RecomputeCurve();
        PushUndoIfGestureStart();
        var preset = EnsureEditablePreset();
        if (preset is null)
        {
            return;
        }

        _editDebounce?.Dispose();
        _editDebounce = _services.Scheduler.Schedule(TimeSpan.FromMilliseconds(350), () => UiDispatch.Post(() =>
        {
            _editDebounce = null; // gesture over — the next tweak starts a new undo step
            if (preset.Mode == EqMode.Parametric)
            {
                preset.Bands = Bands.Select(b => b.ToBand()).ToList();
            }

            _services.Presets.Save(preset);
            _services.Controller?.NotifyPresetContentChanged();
        }));
    }

    private void OnApplied(Core.Configio.ApplyResult result)
    {
        var endpointId = _services.Controller?.State.ActiveEndpointId;
        var report = result.Compilation.Devices.FirstOrDefault(d => d.EndpointId == endpointId)
                     ?? result.Compilation.Devices.FirstOrDefault();
        if (report is null)
        {
            HeadroomSummary = "";
            SafetyNote = "";
            return;
        }

        HeadroomSummary = $"Headroom {report.Headroom.HeadroomDb:0.0} dB";
        SafetyNote = report.Headroom.AutoTrimDb > 0.05
            ? $"Clipping protection active: preamp auto-trimmed by {report.Headroom.AutoTrimDb:0.0} dB."
            : "";
    }

    // ---- Response curve ----

    private void RecomputeCurve()
    {
        var preset = SelectedPreset;
        if (preset is null)
        {
            CurvePoints = [];
            return;
        }

        const int n = 240;
        var points = new List<Point>(n);
        var logMin = Math.Log10(20.0);
        var logMax = Math.Log10(20000.0);

        List<BiquadCoefficients>? coefficients = null;
        List<GraphicEqPoint>? graphic = null;
        if (preset.Mode == EqMode.Parametric)
        {
            coefficients = Bands.Where(b => b.Enabled)
                .Select(b => Biquad.Design(b.Type, 48000, b.FrequencyHz, b.GainDb, b.Q <= 0 ? 1 : b.Q))
                .ToList();
        }
        else
        {
            var freqs = Core.Presets.GraphicBands.FrequenciesFor(preset.Mode);
            graphic = freqs.Select((f, i) => new GraphicEqPoint(f, i < preset.GraphicGains.Length ? preset.GraphicGains[i] : 0))
                .ToList();
        }

        for (var i = 0; i < n; i++)
        {
            var logF = logMin + (logMax - logMin) * i / (n - 1);
            var f = Math.Pow(10, logF);
            double db = 0;
            if (coefficients is not null)
            {
                foreach (var c in coefficients)
                {
                    db += Biquad.MagnitudeDb(c, 48000, f);
                }
            }
            else if (graphic is { Count: > 0 })
            {
                db += InterpolateGraphic(graphic, f);
            }

            points.Add(new Point(logF, db));
        }

        CurvePoints = points;
    }

    private static double InterpolateGraphic(List<GraphicEqPoint> points, double f)
    {
        if (f <= points[0].FrequencyHz)
        {
            return points[0].GainDb;
        }

        if (f >= points[^1].FrequencyHz)
        {
            return points[^1].GainDb;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (f <= points[i].FrequencyHz)
            {
                var f0 = Math.Log10(points[i - 1].FrequencyHz);
                var f1 = Math.Log10(points[i].FrequencyHz);
                var t = (Math.Log10(f) - f0) / (f1 - f0);
                return points[i - 1].GainDb + t * (points[i].GainDb - points[i - 1].GainDb);
            }
        }

        return 0;
    }
}
