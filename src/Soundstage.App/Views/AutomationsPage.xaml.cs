using System.Windows.Controls;
using Soundstage.App.ViewModels;

namespace Soundstage.App.Views;

public partial class AutomationsPage : UserControl
{
    public AutomationsPage(AppServices services)
    {
        DataContext = new AutomationsViewModel(services);
        InitializeComponent();
    }
}
