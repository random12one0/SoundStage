using System;
using System.Windows;
using Soundstage.App.Services;
using Soundstage.App.Tests.Support;
using Soundstage.App.Views;
using Xunit;

namespace Soundstage.App.Tests;

/// <summary>
/// Parses every page's XAML by constructing it on an STA thread. Catches markup errors,
/// bad bindings paths in resources, and missing converters without needing a display.
/// </summary>
public class XamlSmokeTests
{
    [Fact]
    public void AllPages_ParseAndConstruct()
    {
        UiDispatch.Post = action => action();
        StaTestHelper.Run(() =>
        {
            // Pages use app-level styles (Card, SectionTitle, …) that App.xaml loads at
            // runtime. Mirror that here so the smoke test exercises the same resource graph.
            var app = Application.Current ?? new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Soundstage;component/Resources/Theme.xaml", UriKind.Absolute),
            });

            using var services = AppServices.Build();
            _ = new DashboardPage(services);
            _ = new EffectsPage(services);
            _ = new AutomationsPage(services);
            _ = new DevicesPage(services);
            _ = new DiagnosticsPage(services);
            _ = new SettingsPage(services);
        });
    }

    [Fact]
    public void HotkeyParser_HandlesCommonCombos()
    {
        Assert.True(HotkeyService.TryParse("Ctrl+Alt+B", out var mods, out var key));
        Assert.Equal(0x0003u, mods);
        Assert.Equal(System.Windows.Input.Key.B, key);

        Assert.True(HotkeyService.TryParse("Ctrl+Shift+F9", out _, out var f9));
        Assert.Equal(System.Windows.Input.Key.F9, f9);

        Assert.False(HotkeyService.TryParse("Ctrl+", out _, out _));
        Assert.False(HotkeyService.TryParse("banana", out _, out _));
    }
}
