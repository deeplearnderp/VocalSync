using System.ComponentModel;
using System.Windows;
using VocalSync.ViewModels;

namespace VocalSync;

/// <summary>
/// Interaction logic for MainWindow.xaml.
///
/// The <see cref="Views.SignalVisualizer"/> is a performance-sensitive custom
/// control that bypasses the WPF binding system intentionally (it writes pixels
/// directly into a WriteableBitmap). We observe <see cref="MainViewModel"/>
/// property changes here and call the control's Update/Clear methods directly,
/// keeping all rendering on the dispatcher thread without any extra overhead.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Forward visualization data from ViewModel to the custom control.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ToggleCapture();

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.RefreshDevices();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.OpenSettings(this);

    private void RecordButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ToggleRecording();

    private void PlayButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.TogglePlayback();

    private void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AnalyzeLastRecording(this);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // WaveformDisplay changes every frame — use it as the single trigger
        // to push both level and waveform into the control in one pass.
        if (e.PropertyName == nameof(MainViewModel.WaveformDisplay))
        {
            float[] waveform = _viewModel.WaveformDisplay;

            if (waveform.Length == 0)
                Visualizer.Clear();
            else
                Visualizer.Update((float)_viewModel.InputLevel, waveform);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
