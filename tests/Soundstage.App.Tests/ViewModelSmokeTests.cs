using Soundstage.App.Services;
using Soundstage.App.ViewModels;
using Xunit;

namespace Soundstage.App.Tests;

/// <summary>
/// Constructs the real composition root and every viewmodel on a CI machine that has no
/// audio device and no Equalizer APO — the app must come up in guidance mode, not crash.
/// </summary>
public class ViewModelSmokeTests : IDisposable
{
    private readonly AppServices _services;

    public ViewModelSmokeTests()
    {
        UiDispatch.Post = action => action(); // no dispatcher in tests
        _services = AppServices.Build();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public void CompositionRoot_Builds_WithoutApoOrAudio()
    {
        Assert.NotNull(_services.Presets);
        Assert.True(_services.AutoEq.Count > 5000);
        // On CI there is no APO; on a dev machine there may be. Either state must be coherent.
        Assert.Equal(_services.Controller is not null, _services.ApoAvailable);
    }

    [Fact]
    public void AllViewModels_Construct()
    {
        _ = new StatusBarViewModel(_services);
        _ = new RevertToastViewModel(_services);
        _ = new DashboardViewModel(_services);
        _ = new EffectsViewModel(_services);
        _ = new AutomationsViewModel(_services);
        _ = new DevicesViewModel(_services);
        _ = new DiagnosticsViewModel(_services);
        _ = new SettingsViewModel(_services);
    }

    [Fact]
    public void Dashboard_ExposesBundledPresets_AndAutoEqSearch()
    {
        var vm = new DashboardViewModel(_services);
        Assert.True(vm.Presets.Count >= 5);
        Assert.Contains(vm.Presets, p => p.Id == "flat");

        vm.AutoEqQuery = "HD 650";
        Assert.NotEmpty(vm.AutoEqResults);
    }

    [Fact]
    public void EditingABuiltInPreset_AutoForksIntoAnEditableCopy()
    {
        var vm = new DashboardViewModel(_services);
        vm.SelectedPreset = vm.Presets.First(p => p.Id == "music");
        Assert.NotEmpty(vm.Bands);

        // Grabbing a dial on a built-in must fork silently, never block.
        vm.Bands[0].GainDb += 1.0;

        Assert.NotNull(vm.SelectedPreset);
        Assert.False(vm.SelectedPreset.IsBuiltIn);
        Assert.StartsWith("music-custom", vm.SelectedPreset.Id);
        Assert.True(vm.IsEditable);

        // The built-in itself is untouched.
        var original = _services.Presets.Get("music");
        Assert.NotNull(original);
        Assert.True(original.IsBuiltIn);
    }

    [Fact]
    public void RuleEditor_BuildsARule_FromDefaults()
    {
        var vm = new RuleEditorViewModel(_services.Presets.All, existing: null);
        var built = vm.Build();
        Assert.NotNull(built);
        Assert.NotEmpty(built.Value.Actions);
    }

    [Fact]
    public void RuleEditor_RejectsBadTimes()
    {
        var vm = new RuleEditorViewModel(_services.Presets.All, existing: null)
        {
            TriggerKind = TriggerKindChoice.Schedule,
            StartTimeText = "25:99",
        };
        Assert.Null(vm.Build());
        Assert.NotEmpty(vm.ValidationError);
    }

    [Fact]
    public void Diagnostics_Refreshes_WithoutApo()
    {
        var vm = new DiagnosticsViewModel(_services);
        vm.Refresh();
        Assert.False(string.IsNullOrEmpty(vm.OwnershipText));
    }
}
