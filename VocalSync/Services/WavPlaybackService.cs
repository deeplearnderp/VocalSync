using NAudio.Wave;

namespace VocalSync.Services;

/// <summary>
/// Plays a WAV file through a selected output device using NAudio.
/// Simple fire-and-forget playback — no seeking, no scrubbing.
///
/// All methods must be called from the UI thread.
/// Raises <see cref="PlaybackStopped"/> (on the NAudio output thread) when
/// playback ends naturally or is explicitly stopped.
/// </summary>
public sealed class WavPlaybackService : IDisposable
{
    private const int DesiredLatencyMs = 100;

    private AudioFileReader? _reader;
    private WaveOutEvent?    _waveOut;
    private bool             _disposed;

    // -1 = Windows default output device
    private int _deviceNumber = -1;

    /// <summary>True while a file is actively playing.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>
    /// Raised when playback stops — either naturally (end of file) or via <see cref="Stop"/>.
    /// Raised on the NAudio output thread; marshal to the UI thread before touching UI.
    /// </summary>
    public event Action? PlaybackStopped;

    // ── Device ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the output device number used for future <see cref="Play"/> calls.
    /// If playback is active it continues on the current device until stopped.
    /// </summary>
    public void SetDevice(int deviceNumber) => _deviceNumber = deviceNumber;

    // ── Playback lifecycle ────────────────────────────────────────────────

    /// <summary>
    /// Begins playback of the specified WAV file.
    /// Stops any current playback first.
    /// Throws if the file cannot be opened or the output device fails.
    /// </summary>
    public void Play(string filePath)
    {
        Stop(); // Clean up any previous session

        _reader = new AudioFileReader(filePath);

        _waveOut = new WaveOutEvent
        {
            DeviceNumber   = _deviceNumber,
            DesiredLatency = DesiredLatencyMs
        };

        _waveOut.PlaybackStopped += OnPlaybackStopped;
        _waveOut.Init(_reader);
        _waveOut.Play();

        IsPlaying = true;
    }

    /// <summary>
    /// Stops playback immediately.
    /// Safe to call when not playing (no-op).
    /// </summary>
    public void Stop()
    {
        if (!IsPlaying) return;

        IsPlaying = false;

        // Unsubscribe before stopping to avoid a spurious PlaybackStopped event
        // being fired as a result of this explicit Stop() call.
        if (_waveOut != null)
            _waveOut.PlaybackStopped -= OnPlaybackStopped;

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _reader?.Dispose();
        _reader = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Natural end-of-file: clean up and notify.
        IsPlaying = false;

        _waveOut?.Dispose();
        _waveOut = null;

        _reader?.Dispose();
        _reader = null;

        PlaybackStopped?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
