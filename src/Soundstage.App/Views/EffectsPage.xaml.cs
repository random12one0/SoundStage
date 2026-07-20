using System.Windows.Controls;
using Soundstage.App.ViewModels;

namespace Soundstage.App.Views;

public partial class EffectsPage : UserControl
{
    public EffectsPage(AppServices services)
    {
        DataContext = new EffectsViewModel(services);
        InitializeComponent();
    }
}
