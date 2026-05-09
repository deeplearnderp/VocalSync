using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using NAudio.Wave;
using VocalSync.Models;
using VocalSync.Services;

namespace VocalSync.ViewModels;

/// <summary>
/// Lightweight ViewModel for MainWindow.
/// Owns the services, wires audio capture → pitch detection → UI properties.
/// No frameworks, no DI container — just simple composition.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AudioCaptureService _audioCapture = new();
    private readonly PitchDetectionService _pitchDetector = new();
    private readonly PitchStabilizer _stabilizer = new();
    private readonly AudioMonitorService _monitor = new();
    private readonly WavRecorderService _recorder = new();
    private readonly WavPlaybackService _playback = new();
    private readonly OfflineAnalysisService _analyser = new();

    private CancellationTokenSource? _workspaceLoadCts;

    // ── Visualization state ────────────────────────────────────────────────
    // Fixed-size snapshot array reused every frame — no per-callback allocation.
    private const int WaveformPoints = 512;
    private readonly float[] _waveformSnapshot = new float[WaveformPoints];

    // Smoothed level for the meter (EMA, same approach as frequency smoothing).
    private const float LevelSmoothingAlpha = 0.35f;
    private float _smoothedLevel;

    // ── Bindable Properties ────────────────────────────────────────────────

    private string _noteName = "--";
    public string NoteName
    {
        get => _noteName;
        private set => SetField(ref _noteName, value);
    }

    private string _frequencyText = "--- Hz";
    public string FrequencyText
    {
        get => _frequencyText;
        private set => SetField(ref _frequencyText, value);
    }

    private string _statusText = "Sing or hum into your microphone";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private string _toggleButtonLabel = "Start";
    public string ToggleButtonLabel
    {
        get => _toggleButtonLabel;
        private set => SetField(ref _toggleButtonLabel, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanRecord));
            }
        }
    }

    // ── Device selection ───────────────────────────────────────────────────

    private List<AudioDevice> _availableDevices = [];
    public List<AudioDevice> AvailableDevices
    {
        get => _availableDevices;
        private set => SetField(ref _availableDevices, value);
    }

    private AudioDevice? _selectedDevice;
    public AudioDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            // Disallow switching while actively capturing
            if (IsRunning) return;
            if (SetField(ref _selectedDevice, value))
                OnPropertyChanged(nameof(CanStart));
        }
    }

    /// <summary>True when a device is selected and capture is not already running.</summary>
    public bool CanStart => !IsRunning && _selectedDevice != null;

    // ── Monitor properties ────────────────────────────────────────────────

    private bool _isMonitoring;
    /// <summary>
    /// Whether microphone monitoring (passthrough to speakers/headphones) is active.
    /// ⚠ Use headphones to avoid feedback when this is enabled.
    /// </summary>
    public bool IsMonitoring
    {
        get => _isMonitoring;
        set
        {
            if (!SetField(ref _isMonitoring, value)) return;
            if (_isMonitoring && IsRunning)
                TryStartMonitor();
            else
                _monitor.Stop();
        }
    }

    private double _monitorVolume = 0.8;
    /// <summary>Monitor output volume in [0, 1].</summary>
    public double MonitorVolume
    {
        get => _monitorVolume;
        set
        {
            if (!SetField(ref _monitorVolume, value)) return;
            _monitor.Volume = (float)_monitorVolume;
        }
    }

    // ── Settings / output device ──────────────────────────────────────────

    /// <summary>
    /// The NAudio WaveOut device number currently used for monitor output.
    /// -1 = Windows default. Persists across monitor start/stop cycles.
    /// </summary>
    public int SelectedOutputDeviceNumber { get; private set; } = -1;

    /// <summary>
    /// Opens the settings modal. If the user confirms a new output device,
    /// applies it to the monitor service immediately (restarting if needed).
    /// Must be called from the UI thread.
    /// </summary>
    public void OpenSettings(System.Windows.Window owner)
    {
        var vm     = new SettingsViewModel(SelectedOutputDeviceNumber);
        var dialog = new VocalSync.Views.SettingsWindow(vm) { Owner = owner };

        if (dialog.ShowDialog() != true) return;
        if (vm.SelectedOutputDevice == null) return;

        int chosen = vm.SelectedOutputDevice.DeviceNumber;
        if (chosen == SelectedOutputDeviceNumber) return;

        SelectedOutputDeviceNumber = chosen;
        _playback.SetDevice(chosen);

        // Delegate restart logic to the service — it handles the active/idle cases.
        try
        {
            _monitor.SetDevice(chosen);
        }
        catch (Exception ex)
        {
            // SetDevice restarted but the new device failed to open.
            // Monitor is now stopped; inform the user.
            _isMonitoring = false;
            OnPropertyChanged(nameof(IsMonitoring));
            System.Windows.MessageBox.Show(
                $"Could not open output device:\n{ex.Message}",
                "VocalSync — Settings",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    // ── Recording properties ──────────────────────────────────────────────

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetField(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(CanRecord));
                OnPropertyChanged(nameof(CanPlayback));
                OnPropertyChanged(nameof(RecordButtonLabel));
            }
        }
    }

    private bool _isPlayingBack;
    public bool IsPlayingBack
    {
        get => _isPlayingBack;
        private set
        {
            if (SetField(ref _isPlayingBack, value))
            {
                OnPropertyChanged(nameof(CanRecord));
                OnPropertyChanged(nameof(CanPlayback));
                OnPropertyChanged(nameof(PlayButtonLabel));
            }
        }
    }

    private string _lastRecordingPath = string.Empty;
    /// <summary>Full path of the most recently completed recording. Empty if none.</summary>
    public string LastRecordingPath
    {
        get => _lastRecordingPath;
        private set
        {
            if (SetField(ref _lastRecordingPath, value))
            {
                OnPropertyChanged(nameof(LastRecordingName));
                RaisePlaybackTargetChanged();
            }
        }
    }

    /// <summary>Filename only (no path), shown in the UI. Empty string when no recording exists.</summary>
    public string LastRecordingName => string.IsNullOrEmpty(_lastRecordingPath)
        ? string.Empty
        : Path.GetFileName(_lastRecordingPath);

    /// <summary>Recording is only possible when capture is running and not already recording/playing back.</summary>
    public bool CanRecord => IsRunning && !_isPlayingBack;

    /// <summary>Playback targets the library selection when set; otherwise the last saved recording.</summary>
    public bool CanPlayback => PlaybackTargetPath != null && !_isRecording;

    public string RecordButtonLabel => _isRecording ? "■ Stop" : "⏺ Rec";
    public string PlayButtonLabel   => _isPlayingBack ? "■ Stop" : "▶ Play";

    /// <summary>In-memory library of WAVs in the Recordings folder (newest first).</summary>
    public ObservableCollection<RecordingEntry> Recordings { get; } = [];

    private RecordingEntry? _selectedRecording;
    /// <summary>When set, loads offline analysis into the workspace panel (embedded, not a popup).</summary>
    public RecordingEntry? SelectedRecording
    {
        get => _selectedRecording;
        set
        {
            if (value != null
                && _selectedRecording != null
                && string.Equals(value.FilePath, _selectedRecording.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!ReferenceEquals(_selectedRecording, value))
                {
                    _selectedRecording = value;
                    OnPropertyChanged();
                }
                return;
            }

            if (ReferenceEquals(_selectedRecording, value)) return;

            _selectedRecording = value;
            OnPropertyChanged();
            RaisePlaybackTargetChanged();

            _workspaceLoadCts?.Cancel();
            _workspaceLoadCts = new CancellationTokenSource();
            _ = LoadWorkspaceAsync(_workspaceLoadCts.Token);
        }
    }

    /// <summary>Fired when the embedded workspace should show new analysis or clear (null).</summary>
    public event Action<PitchPoint[]?, string?>? WorkspaceSessionChanged;

    /// <summary>Effective path for the main Play button.</summary>
    public string? PlaybackTargetPath
    {
        get
        {
            if (_selectedRecording != null && File.Exists(_selectedRecording.FilePath))
                return _selectedRecording.FilePath;
            if (!string.IsNullOrEmpty(_lastRecordingPath) && File.Exists(_lastRecordingPath))
                return _lastRecordingPath;
            return null;
        }
    }

    public string PlaybackFileNameDisplay =>
        string.IsNullOrEmpty(PlaybackTargetPath) ? string.Empty : Path.GetFileName(PlaybackTargetPath);

    // InputLevel: 0.0–1.0, pre-smoothed for the level meter.
    private double _inputLevel;
    public double InputLevel
    {
        get => _inputLevel;
        private set => SetField(ref _inputLevel, value);
    }

    // WaveformSnapshot: downsampled PCM buffer for the waveform control.
    // The array reference changes each frame so the control always sees a new value.
    private float[] _waveformDisplay = [];
    public float[] WaveformDisplay
    {
        get => _waveformDisplay;
        private set => SetField(ref _waveformDisplay, value);
    }

    // ── Constructor ────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _audioCapture.BufferReady += OnBufferReady;
        _audioCapture.RawBufferReady += OnRawBufferReady;
        _playback.PlaybackStopped += OnPlaybackStopped;
        RefreshDevices();
        RefreshRecordingLibrary(selectPath: null);
    }

    // ── Commands ───────────────────────────────────────────────────────────

    public void ToggleCapture()
    {
        if (!IsRunning)
            StartCapture();
        else
            StopCapture();
    }

    /// <summary>
    /// Re-enumerates audio input devices and updates <see cref="AvailableDevices"/>.
    /// Preserves the current selection if the same device is still present.
    /// Safe to call while not capturing; no-op if capturing is active.
    /// </summary>
    public void RefreshDevices()
    {
        if (IsRunning) return;

        var devices = AudioCaptureService.GetDevices();
        AvailableDevices = devices;

        if (devices.Count == 0)
        {
            SelectedDevice = null;
            StatusText = "No input devices found";
            return;
        }

        // Try to preserve current selection by device number
        int currentNumber = _selectedDevice?.DeviceNumber ?? -1;
        AudioDevice? match = devices.Find(d => d.DeviceNumber == currentNumber);
        SelectedDevice = match ?? devices[0];

        if (_selectedDevice != null)
            StatusText = "Sing or hum into your microphone";
    }

    // ── Recording commands ────────────────────────────────────────────────

    /// <summary>
    /// Toggles WAV recording on/off. Recording requires capture to be running.
    /// Calling while playback is active is a no-op (state guard).
    /// </summary>
    public void ToggleRecording()
    {
        if (_isPlayingBack) return;

        if (!_isRecording)
            StartRecording();
        else
            StopRecording();
    }

    private string _currentRecordingPath = string.Empty;

    private void StartRecording()
    {
        if (!IsRunning || _isRecording) return;

        try
        {
            string folder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Recordings");
            Directory.CreateDirectory(folder);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(folder, $"VocalSync_{timestamp}.wav");

            _recorder.StartRecording(path);
            _currentRecordingPath = path;
            IsRecording = true;
            StatusText = "Recording...";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not start recording:\n{ex.Message}",
                "VocalSync",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopRecording()
    {
        if (!_isRecording) return;

        _recorder.StopRecording();   // finalises and closes the WAV file

        IsRecording = false;
        LastRecordingPath = _currentRecordingPath;
        _currentRecordingPath = string.Empty;
        StatusText = $"Saved: {LastRecordingName}";
        RefreshRecordingLibrary(selectPath: LastRecordingPath);
    }

    /// <summary>
    /// Plays back the most recently completed recording.
    /// No-op if recording is in progress or no recording exists.
    /// </summary>
    public void TogglePlayback()
    {
        if (_isRecording) return;

        if (!_isPlayingBack)
            StartPlayback();
        else
            StopPlayback();
    }

    private void StartPlayback()
    {
        string? path = PlaybackTargetPath;
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path))
        {
            StatusText = "Recording file not found";
            if (string.Equals(path, _lastRecordingPath, StringComparison.OrdinalIgnoreCase))
                LastRecordingPath = string.Empty;
            RefreshRecordingLibrary(selectPath: SelectedRecording?.FilePath);
            RaisePlaybackTargetChanged();
            return;
        }

        try
        {
            _playback.SetDevice(SelectedOutputDeviceNumber);
            _playback.Play(path);
            IsPlayingBack = true;
            StatusText = $"Playing: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not play recording:\n{ex.Message}",
                "VocalSync",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopPlayback()
    {
        _playback.Stop();
        // IsPlayingBack is reset in OnPlaybackStopped
    }

    private void OnPlaybackStopped()
    {
        // Raised on the NAudio output thread — marshal to UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsPlayingBack = false;
            if (IsRunning)
                StatusText = "Listening...";
            else
            {
                string label = PlaybackFileNameDisplay;
                StatusText = string.IsNullOrEmpty(label)
                    ? "Sing or hum into your microphone"
                    : $"Ready  ·  {label}";
            }
        });
    }

    // ── Workspace (embedded analysis) ────────────────────────────────────

    private void RaisePlaybackTargetChanged()
    {
        OnPropertyChanged(nameof(PlaybackTargetPath));
        OnPropertyChanged(nameof(PlaybackFileNameDisplay));
        OnPropertyChanged(nameof(CanPlayback));
    }

    private static string RecordingsDirectory =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings");

    /// <summary>Rebuilds <see cref="Recordings"/> from disk. Optionally selects a path without re-analysing if unchanged.</summary>
    public void RefreshRecordingLibrary(string? selectPath)
    {
        if (!Directory.Exists(RecordingsDirectory))
        {
            Recordings.Clear();
            if (_selectedRecording != null)
                SelectedRecording = null;
            return;
        }

        var entries = new List<RecordingEntry>();
        foreach (string path in Directory.EnumerateFiles(RecordingsDirectory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            var fi = new FileInfo(path);
            string name = fi.Name;
            string kind = name.EndsWith("_corrected.wav", StringComparison.OrdinalIgnoreCase)
                ? "Corrected"
                : "Original";

            DateTime ts = TryParseTimestampFromFilename(name)?.ToUniversalTime()
                          ?? fi.LastWriteTimeUtc;

            entries.Add(new RecordingEntry
            {
                FilePath   = path,
                SourceKind = kind,
                TimestampUtc = ts,
                Duration   = TryGetWavDuration(path)
            });
        }

        entries.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));

        Recordings.Clear();
        foreach (RecordingEntry e in entries)
            Recordings.Add(e);

        if (string.IsNullOrEmpty(selectPath))
            return;

        RecordingEntry? match = entries.Find(e =>
            string.Equals(e.FilePath, selectPath, StringComparison.OrdinalIgnoreCase));

        if (match == null)
            return;

        // Setting SelectedRecording triggers analysis unless path matches current selection.
        SelectedRecording = match;
    }

    private void RaiseWorkspaceSession(PitchPoint[]? points, string? path)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
            WorkspaceSessionChanged?.Invoke(points, path));
    }

    private async Task LoadWorkspaceAsync(CancellationToken cancel)
    {
        if (_selectedRecording == null)
        {
            RaiseWorkspaceSession(null, null);
            return;
        }

        string path = _selectedRecording.FilePath;
        RaiseWorkspaceSession(null, null);

        PitchPoint[] points;
        try
        {
            points = await Task.Run(() => _analyser.Analyse(path), cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"Analysis failed:\n{ex.Message}",
                    "VocalSync",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));
            RaiseWorkspaceSession(null, null);
            return;
        }

        if (cancel.IsCancellationRequested) return;

        if (_selectedRecording == null
            || !string.Equals(_selectedRecording.FilePath, path, StringComparison.OrdinalIgnoreCase))
            return;

        if (points.Length == 0)
        {
            Application.Current?.Dispatcher.Invoke(() =>
                MessageBox.Show(
                    "No audio data found in the recording.",
                    "VocalSync — Analysis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information));
            RaiseWorkspaceSession(null, null);
            return;
        }

        RaiseWorkspaceSession(points, path);
    }

    private static DateTime? TryParseTimestampFromFilename(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith("_corrected", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^"_corrected".Length];

        const string prefix = "VocalSync_";
        if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        string rest = stem[prefix.Length..];
        if (rest.Length < 15 || rest[8] != '_') return null;

        if (DateTime.TryParseExact(
                rest.AsSpan(0, 15),
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime dt))
            return dt;

        return null;
    }

    private static TimeSpan? TryGetWavDuration(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            return reader.TotalTime;
        }
        catch
        {
            return null;
        }
    }

    private void StartCapture()
    {
        if (_selectedDevice == null)
        {
            StatusText = "No input device selected";
            return;
        }

        try
        {
            _audioCapture.Start(_selectedDevice.DeviceNumber);
            IsRunning = true;
            ToggleButtonLabel = "Stop";

            // Start monitor only if the toggle is already on
            if (_isMonitoring)
                TryStartMonitor();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open device \"{_selectedDevice.Name}\":\n{ex.Message}",
                "VocalSync",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Device may have been unplugged — refresh the list
            RefreshDevices();
        }
    }

    /// <summary>
    /// Attempts to start the monitor output. If the output device fails to open,
    /// shows an error and turns the monitoring toggle off so the user sees the state.
    /// </summary>
    private void TryStartMonitor()
    {
        try
        {
            _monitor.Volume = (float)_monitorVolume;
            _monitor.Start();
        }
        catch (Exception ex)
        {
            _isMonitoring = false;           // update backing field without re-entering set
            OnPropertyChanged(nameof(IsMonitoring));
            MessageBox.Show(
                $"Could not open output device for monitoring:\n{ex.Message}",
                "VocalSync — Monitor Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void StopCapture()
    {
        // Finalise any in-progress recording before stopping capture
        if (_isRecording)
            StopRecording();

        _audioCapture.Stop();
        _monitor.Stop();
        _stabilizer.Reset();
        _smoothedLevel = 0f;
        IsRunning = false;
        ToggleButtonLabel = "Start";
        NoteName = "--";
        FrequencyText = "--- Hz";
        StatusText = string.IsNullOrEmpty(_lastRecordingPath)
            ? "Sing or hum into your microphone"
            : $"Ready  ·  {LastRecordingName}";
        InputLevel = 0;
        WaveformDisplay = [];
    }

    // ── Audio Pipeline ─────────────────────────────────────────────────────

    /// <summary>
    /// Forwards raw PCM bytes to the monitor service and recorder.
    /// Called on the NAudio capture thread — must stay non-blocking.
    /// Both Submit() calls are non-blocking by contract.
    /// </summary>
    private void OnRawBufferReady(byte[] buffer, int bytesRecorded)
    {
        _monitor.Submit(buffer, bytesRecorded);
        _recorder.Submit(buffer, bytesRecorded);
    }

    private void OnBufferReady(float[] samples, int count)
    {
        // --- Pitch pipeline (unchanged) ---
        float rawFrequency = _pitchDetector.Detect(samples, count);
        StabilizedPitch stable = _stabilizer.Process(rawFrequency);

        // --- Level meter: RMS → smoothed 0–1 ---
        float rms = 0f;
        for (int i = 0; i < count; i++)
            rms += samples[i] * samples[i];
        rms = MathF.Sqrt(rms / count);

        // EMA smoothing on the audio thread is fine — it's just a float field.
        _smoothedLevel = LevelSmoothingAlpha * rms + (1f - LevelSmoothingAlpha) * _smoothedLevel;
        float level = Math.Clamp(_smoothedLevel * 4f, 0f, 1f); // ×4: RMS headroom scaling

        // --- Waveform: downsample into the fixed reuse buffer ---
        // We reuse _waveformSnapshot to avoid allocating, but pass a fresh
        // array copy to the UI so the control always gets a distinct reference.
        DownsampleInto(samples, count, _waveformSnapshot, WaveformPoints);
        float[] snapshot = (float[])_waveformSnapshot.Clone(); // one alloc per frame, fixed size

        // --- Marshal to UI thread ---
        Application.Current?.Dispatcher.BeginInvoke(() => ApplyResult(stable, level, snapshot));
    }

    private void ApplyResult(StabilizedPitch stable, float level, float[] waveform)
    {
        // Pitch / note display
        if (stable.HasPitch)
        {
            NoteName = stable.NoteName;
            FrequencyText = $"{stable.FrequencyHz:F1} Hz";
            StatusText = "Listening...";
        }
        else
        {
            NoteName = "--";
            FrequencyText = "--- Hz";
            StatusText = "No pitch detected";
        }

        // Visualization properties — always update regardless of pitch state
        InputLevel = level;
        WaveformDisplay = waveform;
    }

    /// <summary>
    /// Nearest-neighbour downsample from <paramref name="src"/> (length <paramref name="srcCount"/>)
    /// into <paramref name="dst"/> (length <paramref name="dstLength"/>).
    /// Writes in-place; no allocation.
    /// </summary>
    private static void DownsampleInto(float[] src, int srcCount, float[] dst, int dstLength)
    {
        if (srcCount == 0 || dstLength == 0) return;
        for (int i = 0; i < dstLength; i++)
        {
            int si = (int)((float)i / dstLength * srcCount);
            dst[i] = src[Math.Clamp(si, 0, srcCount - 1)];
        }
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        _workspaceLoadCts?.Cancel();
        _workspaceLoadCts?.Dispose();
        _audioCapture.Dispose();
        _monitor.Dispose();
        _recorder.Dispose();
        _playback.Dispose();
    }
}
