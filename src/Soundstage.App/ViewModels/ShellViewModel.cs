using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.App.Views;

namespace Soundstage.App.ViewModels;

/// <summary>One entry in the left navigation rail. Glyphs are Segoe MDL2 codepoints.</summary>
public sealed record NavItem(string Key, string Title, string Glyph, Func<UserControl> PageFactory);

/// <summary>Window chrome: navigation, the APO banner, and page hosting.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<string, UserControl> _pages = [];

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem? _selectedItem;

    [ObservableProperty]
    private UserControl? _currentPage;

    [ObservableProperty]
    private bool _showApoMissingBanner;

    [ObservableProperty]
    private bool _showTakeoverBanner;

    [ObservableProperty]
    private bool _showUpdateBanner;

    [ObservableProperty]
    private string _updateBannerText = "";

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _isBypassed;

    public StatusBarViewModel StatusBar { get; }

    public RevertToastViewModel RevertToast { get; }

    public ShellViewModel(AppServices services)
    {
        _services = services;
        StatusBar = new StatusBarViewModel(services);
        RevertToast = new RevertToastViewModel(services);

        NavItems =
        [
            new NavItem("dashboard", "Dashboard", "", () => new DashboardPage(services)),
            new NavItem("effects", "Effects", "", () => new EffectsPage(services)),
            new NavItem("automations", "Automations", "", () => new AutomationsPage(services)),
            new NavItem("devices", "Devices", "", () => new DevicesPage(services)),
            new NavItem("diagnostics", "Diagnostics", "", () => new DiagnosticsPage(services)),
            new NavItem("settings", "Settings", "", () => new SettingsPage(services)),
        ];

        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(RefreshBanners);
        }

        services.Update.Changed += () => UiDispatch.Post(RefreshBanners);

        SelectedItem = NavItems[0];
        RefreshBanners();
    }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        if (!_pages.TryGetValue(value.Key, out var page))
        {
            page = value.PageFactory();
            _pages[value.Key] = page;
        }

        CurrentPage = page;
    }

    public void NavigateTo(string key) => SelectedItem = NavItems.FirstOrDefault(i => i.Key == key) ?? SelectedItem;

    public void RefreshBanners()
    {
        ShowApoMissingBanner = !_services.ApoAvailable;
        ShowTakeoverBanner = _services.ApoAvailable
                             && _services.Takeover?.GetOwnershipState() is not Core.Configio.OwnershipState.Owned;
        ShowUpdateBanner = _services.Update.IsUpdateAvailable;
        UpdateBannerText = _services.Update.Latest is { } latest ? $"{latest.Name} is available." : "An update is available.";
        CanUndo = _services.Controller?.CanUndo ?? false;
        IsBypassed = _services.Controller?.State.BypassActive ?? false;
    }

    [RelayCommand]
    private void OpenUpdateSettings() => NavigateTo("settings");

    [RelayCommand]
    private void ToggleBypass() => _services.Controller?.ToggleBypass();

    [RelayCommand]
    private void TakeOwnership()
    {
        try
        {
            _services.Takeover?.TakeOwnership();
            _services.Controller?.Apply(Core.State.ApplyAttribution.System("took ownership"));
        }
        catch
        {
            NavigateTo("diagnostics");
        }

        RefreshBanners();
    }

    [RelayCommand]
    private void OpenDiagnostics() => NavigateTo("diagnostics");

    /// <summary>Global Ctrl+Z — steps the sound back one change.</summary>
    [RelayCommand]
    private void Undo() => _services.Controller?.Undo();
}
