using NAudio.Wave;
using VocalSync.Models;

namespace VocalSync.Services;

/// <summary>
/// Plays captured microphone audio back through a selected output device in realtime.
///
/// Pipeline:
///   AudioCaptureService.RawBufferReady
///       → AudioMonitorService.Submit()   (audio callback thread — non-blocking)
///       → BufferedWaveProvider           (NAudio internal queue)
///       → WaveOutEvent                   (output thread — pulls independently)
///
/// <see cref="Submit"/> is safe to call from the audio callback thread.
/// All other methods must be called from the UI thread.
///
/// ⚠ FEEDBACK WARNING: Using speakers instead of headphones while monitoring
///   will cause microphone feedback (howling). Always use headphones when
///   monitoring is enabled.
/// </summary>
public sealed class AudioMonitorService : IDisposable
{
    // Must match AudioCaptureService exactly so the provider format is correct.
    private const int SampleRate  = 44100;
    private const int Channels    = 1;
    private const int BitDepth    = 16;

    // Buffer ceiling: how many milliseconds of audio the provider will hold.
    // Overflow beyond this is silently discarded (old data removed first)
    // to keep end-to-end latency bounded rather than growing without limit.
    private const int ProviderBufferMs = 200;

    // WaveOutEvent latency: the output device's own internal buffer size.
    // 50 ms matches the capture buffer interval, keeping total latency ~100 ms.
    private const int OutputLatencyMs = 50;

    private static readonly WaveFormat WaveFormat =
        new(SampleRate, BitDepth, Channels);

    private BufferedWaveProvider? _buffer;
    private WaveOutEvent?         _waveOut;
    private bool                  _disposed;

    // -1 = Windows default output device (WaveOutEvent default)
    private int _deviceNumber = -1;

    public bool IsPlaying { get; private set; }

    // ── Output device enumeration ─────────────────────────────────────────

    /// <summary>
    /// Returns all available audio output (playback) devices, plus a
    /// "System Default" entry at index -1 as the first item.
    /// Never throws — returns whatever was enumerated before any error.
    /// </summary>
    public static List<OutputDevice> GetOutputDevices()
    {
        var devices = new List<OutputDevice>
        {
            new(-1, "System Default")
        };

        try
        {
            int count = WaveOut.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                WaveOutCapabilities caps = WaveOut.GetCapabilities(i);
                devices.Add(new OutputDevice(i, caps.ProductName));
            }
        }
        catch
        {
            // Return the default entry plus whatever was collected before the error.
        }

        return devices;
    }

    // ── Output device switching ───────────────────────────────────────────

    /// <summary>
    /// Switches the output device. If playback is currently active, it is
    /// stopped, the device is changed, and playback is restarted transparently.
    /// Safe to call from the UI thread at any time.
    /// </summary>
    /// <param name="deviceNumber">
    ///   NAudio WaveOut device index, or -1 for the Windows default.
    /// </param>
    public void SetDevice(int deviceNumber)
    {
        if (_deviceNumber == deviceNumber) return;
        _deviceNumber = deviceNumber;

        if (!IsPlaying) return;

        // Restart playback on the new device.
        // Stop() sets IsPlaying=false and nulls _buffer before Submit() can
        // observe a non-null _buffer with the wrong device, so there is no race.
        Stop();
        Start();
    }

    // ── Volume ────────────────────────────────────────────────────────────

    private float _volume = 0.8f;

    /// <summary>
    /// Output volume in [0, 1]. Applied immediately to the active WaveOutEvent.
    /// Safe to set from any thread (float write is atomic on x86/x64).
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_waveOut != null)
                _waveOut.Volume = _volume;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the output pipeline. Safe to call even if already playing (no-op).
    /// Throws if the output device cannot be opened.
    /// </summary>
    public void Start()
    {
        if (IsPlaying) return;

        _buffer = new BufferedWaveProvider(WaveFormat)
        {
            BufferDuration      = TimeSpan.FromMilliseconds(ProviderBufferMs),
            DiscardOnBufferOverflow = true   // drop oldest audio rather than blocking
        };

        _waveOut = new WaveOutEvent
        {
            DeviceNumber   = _deviceNumber,
            DesiredLatency = OutputLatencyMs,
            Volume         = _volume
        };

        _waveOut.Init(_buffer);
        _waveOut.Play();

        IsPlaying = true;
    }

    /// <summary>
    /// Stops playback and releases the output device.
    /// Safe to call even if not playing (no-op).
    /// </summary>
    public void Stop()
    {
        if (!IsPlaying) return;

        IsPlaying = false;

        // Stop the output before disposing the provider to avoid an underrun log.
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _buffer?.ClearBuffer();
        _buffer = null;
    }

    // ── Audio path ────────────────────────────────────────────────────────

    /// <summary>
    /// Submits raw 16-bit PCM bytes from the capture callback into the playback buffer.
    /// Non-blocking — <see cref="BufferedWaveProvider.AddSamples"/> never waits.
    /// Must be called with the same format as the capture device (44.1 kHz, 16-bit mono).
    ///
    /// If monitoring is stopped between the caller's null-check and this call,
    /// <see cref="IsPlaying"/> is checked here and the call is silently dropped.
    /// </summary>
    public void Submit(byte[] buffer, int bytesRecorded)
    {
        // Guard: IsPlaying is set to false before _buffer is nulled in Stop(),
        // so this check reliably prevents a NullReferenceException.
        if (!IsPlaying || _buffer == null) return;

        _buffer.AddSamples(buffer, 0, bytesRecorded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
