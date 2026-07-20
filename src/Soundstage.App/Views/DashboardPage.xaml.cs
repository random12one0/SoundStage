using System.Windows.Controls;
using Soundstage.App.ViewModels;

namespace Soundstage.App.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage(AppServices services)
    {
        DataContext = new DashboardViewModel(services);
        InitializeComponent();
    }
}
