using System.Windows;
using Soundstage.App.ViewModels;
using Soundstage.Core.Automation;
using Wpf.Ui.Controls;

namespace Soundstage.App.Views;

public partial class RuleEditorWindow : FluentWindow
{
    private readonly RuleEditorViewModel _viewModel;
    private readonly AutomationRule? _existing;

    /// <summary>The freshly built rule when creating (null while editing in place).</summary>
    public AutomationRule? Result { get; private set; }

    public RuleEditorWindow(AppServices services, AutomationRule? existing)
    {
        _existing = existing;
        _viewModel = new RuleEditorViewModel(services.Presets.All, existing, id => services.Presets.Get(id)?.Name ?? id);
        DataContext = _viewModel;
        InitializeComponent();

        // Guard against being our own owner: if no other window exists yet, WPF has already
        // made THIS window the app's MainWindow, and Owner = MainWindow would throw.
        if (Application.Current?.MainWindow is { } main && !ReferenceEquals(main, this))
        {
            Owner = main;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var built = _viewModel.Build();
        if (built is null)
        {
            return; // validation error is showing
        }

        var (trigger, actions) = built.Value;
        if (_existing is not null)
        {
            _existing.Name = _viewModel.RuleName;
            _existing.Trigger = trigger;
            _existing.Actions = actions;
        }
        else
        {
            Result = new AutomationRule
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = _viewModel.RuleName,
                Trigger = trigger,
                Actions = actions,
                Enabled = true,
            };
        }

        DialogResult = true;
    }
}
