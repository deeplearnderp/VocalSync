using VocalSync.Models;
using VocalSync.Services;

namespace VocalSync.ViewModels;

/// <summary>
/// ViewModel for the settings dialog.
///
/// Deliberately minimal — no INotifyPropertyChanged, because SettingsWindow
/// is a modal dialog whose values are read once on close. The ComboBox
/// binding works via standard WPF two-way binding to a plain property.
/// </summary>
public sealed class SettingsViewModel
{
    /// <summary>All available audio output (playback) devices.</summary>
    public List<OutputDevice> AvailableOutputDevices { get; }

    /// <summary>
    /// The output device selected by the user.
    /// Defaults to the "System Default" entry (DeviceNumber = -1).
    /// </summary>
    public OutputDevice? SelectedOutputDevice { get; set; }

    public SettingsViewModel(int currentOutputDeviceNumber)
    {
        AvailableOutputDevices = AudioMonitorService.GetOutputDevices();

        // Pre-select the device that is currently in use, falling back to System Default.
        SelectedOutputDevice =
            AvailableOutputDevices.Find(d => d.DeviceNumber == currentOutputDeviceNumber)
            ?? AvailableOutputDevices[0];
    }
}
