namespace VocalSync.Models;

/// <summary>
/// Represents a single audio input device as enumerated by NAudio.
/// Immutable — a new instance is created on each enumeration pass.
/// </summary>
public sealed class AudioDevice
{
    /// <summary>NAudio WaveIn device index (passed to WaveInEvent.DeviceNumber).</summary>
    public int DeviceNumber { get; }

    /// <summary>Friendly product name reported by the driver.</summary>
    public string Name { get; }

    public AudioDevice(int deviceNumber, string name)
    {
        DeviceNumber = deviceNumber;
        Name = name;
    }

    // Used by ComboBox to display the item when ItemsSource is bound directly.
    public override string ToString() => Name;
}
