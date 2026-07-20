using System.Windows.Controls;
using Soundstage.App.ViewModels;

namespace Soundstage.App.Views;

public partial class DiagnosticsPage : UserControl
{
    public DiagnosticsPage(AppServices services)
    {
        DataContext = new DiagnosticsViewModel(services);
        InitializeComponent();
    }
}
