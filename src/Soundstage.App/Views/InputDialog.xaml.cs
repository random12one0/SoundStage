using System.Windows;
using Wpf.Ui.Controls;

namespace Soundstage.App.Views;

public partial class InputDialog : FluentWindow
{
    public InputDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Shows the dialog and returns the entered text, or null on cancel.</summary>
    public static string? Show(string title, string prompt, string initialValue = "")
    {
        var dialog = new InputDialog(title, prompt, initialValue)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
