namespace VocalSync.Models;

/// <summary>
/// Represents a single audio output (playback) device as enumerated by NAudio.
/// Immutable — a new instance is created on each enumeration pass.
/// </summary>
public sealed class OutputDevice
{
    /// <summary>
    /// NAudio WaveOut device index (passed to WaveOutEvent.DeviceNumber).
    /// -1 means the Windows default output device.
    /// </summary>
    public int DeviceNumber { get; }

    /// <summary>Friendly product name reported by the driver.</summary>
    public string Name { get; }

    public OutputDevice(int deviceNumber, string name)
    {
        DeviceNumber = deviceNumber;
        Name = name;
    }

    // Used by ComboBox display when ItemsSource is bound directly.
    public override string ToString() => Name;
}
