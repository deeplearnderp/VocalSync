using NAudio.Wave;
using VocalSync.Models;

namespace VocalSync.Services;

/// <summary>
/// Captures mono microphone input at 44.1 kHz using NAudio.
/// Raises BufferReady with float PCM samples whenever a buffer is filled.
///
/// Device selection: call <see cref="GetDevices"/> to enumerate inputs,
/// then pass the chosen <see cref="AudioDevice.DeviceNumber"/> to <see cref="Start"/>.
/// </summary>
public class AudioCaptureService : IDisposable
{
    private const int SampleRate = 44100;
    private const int Channels = 1;
    private const int BufferMilliseconds = 50; // ~2205 samples per callback

    private WaveInEvent? _waveIn;
    private bool _disposed;

    /// <summary>Raised on the NAudio callback thread with float PCM samples.</summary>
    public event Action<float[], int>? BufferReady;

    /// <summary>
    /// Raised on the NAudio callback thread with the raw 16-bit PCM byte buffer
    /// before float conversion. Intended for the monitoring pipeline, which can
    /// pass bytes directly to <see cref="NAudio.Wave.BufferedWaveProvider"/>
    /// without re-encoding.
    /// </summary>
    public event Action<byte[], int>? RawBufferReady;

    public bool IsCapturing { get; private set; }

    // ── Device enumeration ────────────────────────────────────────────────

    /// <summary>
    /// Returns all currently available audio input devices.
    /// Safe to call at any time; never throws — returns an empty list on failure.
    /// </summary>
    public static List<AudioDevice> GetDevices()
    {
        var devices = new List<AudioDevice>();
        try
        {
            int count = WaveIn.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                WaveInCapabilities caps = WaveIn.GetCapabilities(i);
                devices.Add(new AudioDevice(i, caps.ProductName));
            }
        }
        catch
        {
            // If enumeration fails (e.g. driver error), return whatever we have so far.
        }
        return devices;
    }

    // ── Capture control ───────────────────────────────────────────────────

    /// <summary>
    /// Starts microphone capture on the specified device.
    /// </summary>
    /// <param name="deviceNumber">
    ///   NAudio device index from <see cref="AudioDevice.DeviceNumber"/>.
    ///   Use 0 for the system default device.
    /// </param>
    /// <exception cref="InvalidOperationException">Already capturing.</exception>
    /// <exception cref="Exception">NAudio failed to open the device.</exception>
    public void Start(int deviceNumber = 0)
    {
        if (IsCapturing) return;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = BufferMilliseconds
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        IsCapturing = true;
    }

    /// <summary>Stops microphone capture.</summary>
    public void Stop()
    {
        if (!IsCapturing) return;

        _waveIn?.StopRecording();
        // Actual cleanup happens in OnRecordingStopped to avoid races,
        // but we flag IsCapturing=false immediately so callers see the state change.
        IsCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Raw bytes first — monitoring pipeline uses these directly (no re-encoding needed).
        RawBufferReady?.Invoke(e.Buffer, e.BytesRecorded);

        // Convert 16-bit PCM bytes → float samples in [-1.0, 1.0]
        int sampleCount = e.BytesRecorded / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short raw = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            samples[i] = raw / 32768f;
        }

        BufferReady?.Invoke(samples, sampleCount);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Dispose the WaveInEvent after recording has fully stopped.
        // This is the correct NAudio teardown sequence — avoids ObjectDisposedException
        // if Stop() is called while a buffer callback is in flight.
        _waveIn?.Dispose();
        _waveIn = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
