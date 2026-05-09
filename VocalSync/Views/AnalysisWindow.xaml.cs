using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VocalSync.Models;
using VocalSync.Services;

namespace VocalSync.Views;

/// <summary>
/// Displays offline pitch analysis results and hosts the correction render pipeline.
///
/// Graph rendering: unchanged from analysis pass.
/// Correction: owned entirely within this window — no coupling to MainViewModel.
///   The window already holds the PitchPoint[] and source file path, so it has
///   everything PitchCorrectionService needs.
/// Playback of the corrected file: a dedicated WavPlaybackService instance owned
///   by this window, independent of the main capture pipeline.
/// </summary>
public partial class AnalysisWindow : Window
{
    // ── Graph constants ───────────────────────────────────────────────────

    private const int MidiYMin  = 36;  // C2
    private const int MidiYMax  = 84;  // C6
    private const int MidiRange = MidiYMax - MidiYMin;

    private static readonly int ColBackground = Bgra(0x12, 0x12, 0x12);
    private static readonly int ColGrid       = Bgra(0x1E, 0x1E, 0x1E);
    private static readonly int ColGridOctave = Bgra(0x2A, 0x2A, 0x2A);
    private static readonly int ColDotHigh    = Bgra(0x1D, 0xB9, 0x54);
    private static readonly int ColDotLow     = Bgra(0x44, 0x44, 0x44);

    // ── State ─────────────────────────────────────────────────────────────

    private readonly PitchPoint[]           _points;
    private readonly string                 _sourcePath;
    private readonly PitchCorrectionService _corrector = new();
    private readonly WavPlaybackService     _player    = new();

    private string? _correctedPath;
    private bool    _isRendering;

    // ── Constructor ───────────────────────────────────────────────────────

    public AnalysisWindow(PitchPoint[] points, string filePath)
    {
        InitializeComponent();

        _points     = points;
        _sourcePath = filePath;

        PopulateHeader(points, filePath);

        // Update strength label as the slider moves
        StrengthSlider.ValueChanged += (_, e) =>
            StrengthLabel.Text = $"{e.NewValue:P0}";

        // Wire playback-stopped so the button label resets
        _player.PlaybackStopped += () => Dispatcher.BeginInvoke(() =>
        {
            PlayCorrectedButton.Content = "▶ Play Corrected";
        });

        Loaded  += (_, _) => RenderGraph(points);
        Closed  += (_, _) => _player.Dispose();

        // Apply initial disabled state (toggle is unchecked by default)
        UpdateCorrectionControls();
    }

    // ── Header ────────────────────────────────────────────────────────────

    private void PopulateHeader(PitchPoint[] points, string filePath)
    {
        FileNameText.Text = System.IO.Path.GetFileName(filePath);

        int   voiced    = points.Count(p => p.IsVoiced);
        float duration  = points.Length > 0 ? points[^1].TimeSeconds : 0f;
        float voicedPct = points.Length > 0 ? (float)voiced / points.Length * 100f : 0f;

        StatsText.Text = $"{points.Length} frames  ·  {duration:F1}s  ·  "
                       + $"avg confidence: {(voiced > 0 ? points.Where(p => p.IsVoiced).Average(p => p.Confidence) : 0f):P0}";

        VoicedBadge.Text = $"Voiced: {voicedPct:F0}%";

        if (voiced > 0)
        {
            float minHz = points.Where(p => p.IsVoiced).Min(p => p.FrequencyHz);
            float maxHz = points.Where(p => p.IsVoiced).Max(p => p.FrequencyHz);
            StatsText.Text += $"  ·  range: {NoteService.GetNoteName(minHz)} – {NoteService.GetNoteName(maxHz)}";
        }
    }

    // ── Graph rendering (unchanged from analysis pass) ────────────────────

    private void RenderGraph(PitchPoint[] points)
    {
        int w = Math.Max(1, (int)GraphImage.ActualWidth);
        int h = Math.Max(1, (int)GraphImage.ActualHeight);

        var bmp    = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new int[w * h];

        Array.Fill(pixels, ColBackground);

        for (int midi = MidiYMin; midi <= MidiYMax; midi++)
        {
            int y = MidiToY(midi, h);
            if (y < 0 || y >= h) continue;
            int col = (midi % 12 == 0) ? ColGridOctave : ColGrid;
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = col;
        }

        if (points.Length > 1)
        {
            float totalTime = points[^1].TimeSeconds;
            if (totalTime <= 0f) totalTime = 1f;

            foreach (var pt in points)
            {
                if (!pt.IsVoiced) continue;
                if (pt.MidiNote < MidiYMin || pt.MidiNote > MidiYMax) continue;

                int px = (int)((pt.TimeSeconds / totalTime) * (w - 1));
                int py = MidiToY(pt.MidiNote, h);
                int dotColour = BlendColour(ColDotLow, ColDotHigh, pt.Confidence);

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ix = px + dx, iy = py + dy;
                    if (ix >= 0 && ix < w && iy >= 0 && iy < h)
                        pixels[iy * w + ix] = dotColour;
                }
            }
        }

        bmp.Lock();
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Unlock();

        GraphImage.Source = bmp;
    }

    // ── Correction toggle ─────────────────────────────────────────────────

    private void CorrectionToggle_Changed(object sender, RoutedEventArgs e)
        => UpdateCorrectionControls();

    /// <summary>
    /// Applies enabled/disabled state to the entire correction controls grid
    /// based on the toggle checkbox. Called on toggle change and once at startup.
    ///
    /// WPF propagates IsEnabled=false from a parent Grid to all children automatically,
    /// so a single assignment disables the slider, both buttons, and the strength label.
    ///
    /// PlayCorrectedButton has its own additional guard (needs a rendered file) —
    /// re-enabling the toggle does not automatically re-enable it if no file exists yet.
    /// </summary>
    private void UpdateCorrectionControls()
    {
        bool enabled = CorrectionToggle.IsChecked == true;

        CorrectionControlsGrid.IsEnabled = enabled;

        if (!enabled)
        {
            // Stop any in-progress render or playback when the user turns off correction
            if (_isRendering)
            {
                // Render is on a Task.Run — we can't cancel it mid-flight without a
                // CancellationToken (future improvement). Let it finish; the output
                // file is still written, but the UI reflects the disabled state.
            }

            _player.Stop();
            PlayCorrectedButton.Content = "▶ Play Corrected";

            // Only reset the status text if we're not mid-render
            if (!_isRendering)
                CorrectionStatus.Text = "Enable correction above to render";
        }
        else
        {
            // Restore appropriate status text when toggled back on
            CorrectionStatus.Text = _correctedPath != null
                ? $"Saved: {System.IO.Path.GetFileName(_correctedPath)}"
                : "Ready to render";

            // Re-enable play button only if a corrected file already exists
            PlayCorrectedButton.IsEnabled = _correctedPath != null;
        }
    }

    // ── Correction controls ───────────────────────────────────────────────

    private async void RenderButton_Click(object sender, RoutedEventArgs e)
    {
        // Belt-and-suspenders: the button is already disabled when the toggle is off,
        // but guard here too in case of unexpected invocation paths.
        if (_isRendering || CorrectionToggle.IsChecked != true) return;

        // Stop any currently playing corrected file before re-rendering
        _player.Stop();
        PlayCorrectedButton.IsEnabled  = false;
        PlayCorrectedButton.Content    = "▶ Play Corrected";

        _isRendering             = true;
        RenderButton.IsEnabled   = false;
        RenderProgress.Value     = 0;
        RenderProgress.Visibility = Visibility.Visible;
        CorrectionStatus.Text    = "Rendering...";

        float strength = (float)StrengthSlider.Value;

        var progress  = new Progress<float>(v => Dispatcher.BeginInvoke(() =>
            RenderProgress.Value = v));

        string? outPath = null;
        try
        {
            outPath = await Task.Run(() =>
                _corrector.Correct(_sourcePath, _points, strength, progress));
        }
        catch (Exception ex)
        {
            CorrectionStatus.Text = "Render failed";
            RenderProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show(
                $"Correction failed:\n{ex.Message}",
                "VocalSync — Correction",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isRendering           = false;
            RenderButton.IsEnabled = true;
        }

        if (outPath != null)
        {
            _correctedPath             = outPath;
            RenderProgress.Visibility  = Visibility.Collapsed;
            CorrectionStatus.Text      = $"Saved: {System.IO.Path.GetFileName(outPath)}";
            PlayCorrectedButton.IsEnabled = true;
        }
    }

    private void PlayCorrectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_correctedPath == null) return;

        if (_player.IsPlaying)
        {
            _player.Stop();
            PlayCorrectedButton.Content = "▶ Play Corrected";
            return;
        }

        try
        {
            _player.Play(_correctedPath);
            PlayCorrectedButton.Content = "■ Stop";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not play corrected file:\n{ex.Message}",
                "VocalSync — Correction",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ── Coordinate + colour helpers (unchanged) ───────────────────────────

    private static int MidiToY(int midi, int height)
    {
        float norm = 1f - (float)(midi - MidiYMin) / MidiRange;
        return (int)(norm * (height - 1));
    }

    private static int BlendColour(int from, int to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int br = (byte)((from >> 16) & 0xFF), bg = (byte)((from >> 8) & 0xFF), bb = (byte)(from & 0xFF);
        int tr = (byte)((to   >> 16) & 0xFF), tg = (byte)((to   >> 8) & 0xFF), tb = (byte)(to  & 0xFF);
        return (255 << 24) | ((int)(br + (tr - br) * t) << 16)
                           | ((int)(bg + (tg - bg) * t) << 8)
                           |  (int)(bb + (tb - bb) * t);
    }

    private static int Bgra(byte r, byte g, byte b)
        => (255 << 24) | (r << 16) | (g << 8) | b;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
