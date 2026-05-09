using System.Windows;
using VocalSync.ViewModels;

namespace VocalSync.Views;

/// <summary>
/// Modal settings dialog. Opens with a pre-populated <see cref="SettingsViewModel"/>
/// and sets <see cref="DialogResult"/> true on OK so the caller can read the result.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
