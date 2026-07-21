using System.Windows.Controls;
using Soundstage.App.ViewModels;

namespace Soundstage.App.Views;

public partial class SpeakersPage : UserControl
{
    public SpeakersPage(AppServices services)
    {
        DataContext = new SpeakersViewModel(services);
        InitializeComponent();
    }
}
