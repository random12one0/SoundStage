using System.Windows.Controls;
using Soundstage.App.ViewModels;

namespace Soundstage.App.Views;

public partial class DevicesPage : UserControl
{
    public DevicesPage(AppServices services)
    {
        DataContext = new DevicesViewModel(services);
        InitializeComponent();
    }
}
