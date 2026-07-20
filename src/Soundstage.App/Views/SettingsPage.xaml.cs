using System.Windows.Controls;
using Soundstage.App.ViewModels;

namespace Soundstage.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage(AppServices services)
    {
        DataContext = new SettingsViewModel(services);
        InitializeComponent();
    }
}
