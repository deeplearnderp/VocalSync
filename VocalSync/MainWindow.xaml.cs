using System.ComponentModel;
using System.Windows;
using VocalSync.Models;
using VocalSync.ViewModels;

namespace VocalSync;

/// <summary>
/// Interaction logic for MainWindow.
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

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.WorkspaceSessionChanged += OnWorkspaceSessionChanged;

        WorkspacePanel.SetPlaybackDevice(_viewModel.SelectedOutputDeviceNumber);
        WorkspacePanel.AttachWorkspacePlayback(_viewModel);
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ToggleCapture();

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.RefreshDevices();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenSettings(this);
        WorkspacePanel.SetPlaybackDevice(_viewModel.SelectedOutputDeviceNumber);
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ToggleRecording();

    private void PlayButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.TogglePlayback();

    private void OnWorkspaceSessionChanged(PitchPoint[]? points, string? path)
    {
        if (points == null || path == null)
            WorkspacePanel.Clear();
        else
            WorkspacePanel.SetAnalysis(points, path);
    }

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
        else if (e.PropertyName == nameof(MainViewModel.IsPlayingBack))
        {
            WorkspacePanel.OnMainWorkspacePlaybackStateChanged(_viewModel.IsPlayingBack);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        WorkspacePanel.AttachWorkspacePlayback(null);
        _viewModel.WorkspaceSessionChanged -= OnWorkspaceSessionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
