using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VocalSync.Models;
using VocalSync.Services;

namespace VocalSync.Views;

/// <summary>
/// Embedded workspace panel: offline pitch graph, correction render, and corrected playback.
/// </summary>
public partial class AnalysisPanel : UserControl
{
    private const int MidiYMin  = 36;
    private const int MidiYMax  = 84;
    private const int MidiRange = MidiYMax - MidiYMin;

    private static readonly int ColBackground = Bgra(0x12, 0x12, 0x12);
    private static readonly int ColGrid       = Bgra(0x1E, 0x1E, 0x1E);
    private static readonly int ColGridOctave = Bgra(0x2A, 0x2A, 0x2A);
    private static readonly int ColDotHigh    = Bgra(0x1D, 0xB9, 0x54);
    private static readonly int ColDotLow     = Bgra(0x44, 0x44, 0x44);

    private PitchPoint[] _points = [];
    private string _sourcePath = string.Empty;
    private readonly PitchCorrectionService _corrector = new();
    private readonly WavPlaybackService _player = new();

    private string? _correctedPath;
    private bool _isRendering;

    /// <summary>Matches main / settings output device for corrected playback.</summary>
    public void SetPlaybackDevice(int deviceNumber) => _player.SetDevice(deviceNumber);

    public AnalysisPanel()
    {
        InitializeComponent();

        StrengthSlider.ValueChanged += (_, e) =>
            StrengthLabel.Text = $"{e.NewValue:P0}";

        _player.PlaybackStopped += () => Dispatcher.BeginInvoke(() =>
        {
            PlayCorrectedButton.Content = "▶ Play Corrected";
        });

        Unloaded += (_, _) => _player.Dispose();

        SizeChanged += (_, _) =>
        {
            if (_points.Length > 0 && ActualWidth > 0)
                RenderGraph(_points);
        };

        UpdateCorrectionControls();
    }

    /// <summary>Clears analysis UI (no file loaded).</summary>
    public void Clear()
    {
        ResetCorrectionSession();
        _points = [];
        _sourcePath = string.Empty;

        FileNameText.Text = "—";
        StatsText.Text = "Select a recording in the library";
        VoicedBadge.Text = "—";
        GraphImage.Source = null;
    }

    /// <summary>Loads a completed analysis session into the panel.</summary>
    public void SetAnalysis(PitchPoint[] points, string filePath)
    {
        ResetCorrectionSession();

        _points = points;
        _sourcePath = filePath;

        PopulateHeader(points, filePath);
        Dispatcher.BeginInvoke(() => RenderGraph(points), System.Windows.Threading.DispatcherPriority.Loaded);
        UpdateCorrectionControls();
    }

    private void ResetCorrectionSession()
    {
        _isRendering = false;
        _correctedPath = null;
        _player.Stop();
        PlayCorrectedButton.Content = "▶ Play Corrected";
        PlayCorrectedButton.IsEnabled = false;
        RenderProgress.Visibility = Visibility.Collapsed;
        RenderProgress.Value = 0;
        RenderButton.IsEnabled = true;
        CorrectionToggle.IsChecked = false;
    }

    private void PopulateHeader(PitchPoint[] points, string filePath)
    {
        FileNameText.Text = Path.GetFileName(filePath);

        int voiced = points.Count(p => p.IsVoiced);
        float duration = points.Length > 0 ? points[^1].TimeSeconds : 0f;
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

    private void RenderGraph(PitchPoint[] points)
    {
        int w = Math.Max(1, (int)GraphImage.ActualWidth);
        int h = Math.Max(1, (int)GraphImage.ActualHeight);

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
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

        if (points.Length > 0)
        {
            float totalTime = points.Length > 1 ? points[^1].TimeSeconds : 0f;
            if (totalTime <= 0f) totalTime = 1f;

            // Thin pitch contour: connect consecutive in-range voiced frames; gaps at unvoiced runs.
            bool havePrev = false;
            int prevPx = 0, prevPy = 0;
            float prevConf = 0f;

            for (int i = 0; i < points.Length; i++)
            {
                PitchPoint pt = points[i];
                if (!pt.IsVoiced || pt.MidiNote < MidiYMin || pt.MidiNote > MidiYMax)
                {
                    havePrev = false;
                    continue;
                }

                int px = (int)((pt.TimeSeconds / totalTime) * (w - 1));
                int py = MidiToY(pt.MidiNote, h);

                if (havePrev)
                {
                    int col = BlendColour(ColDotLow, ColDotHigh, (prevConf + pt.Confidence) * 0.5f);
                    DrawLineBresenham(pixels, w, h, prevPx, prevPy, px, py, col);
                }
                else
                {
                    // Segment start: single sample as a 1px anchor (readable when isolated)
                    int col = BlendColour(ColDotLow, ColDotHigh, pt.Confidence);
                    if (px >= 0 && px < w && py >= 0 && py < h)
                        pixels[py * w + px] = col;
                }

                havePrev = true;
                prevPx = px;
                prevPy = py;
                prevConf = pt.Confidence;
            }
        }

        bmp.Lock();
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Unlock();

        GraphImage.Source = bmp;
    }

    private void CorrectionToggle_Changed(object sender, RoutedEventArgs e)
        => UpdateCorrectionControls();

    private void UpdateCorrectionControls()
    {
        bool enabled = CorrectionToggle.IsChecked == true;

        CorrectionControlsGrid.IsEnabled = enabled;

        if (!enabled)
        {
            _player.Stop();
            PlayCorrectedButton.Content = "▶ Play Corrected";

            if (!_isRendering)
                CorrectionStatus.Text = "Enable correction above to render";
        }
        else
        {
            CorrectionStatus.Text = _correctedPath != null
                ? $"Saved: {Path.GetFileName(_correctedPath)}"
                : "Ready to render";

            PlayCorrectedButton.IsEnabled = _correctedPath != null;
        }
    }

    private async void RenderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRendering || CorrectionToggle.IsChecked != true || _points.Length == 0) return;

        _player.Stop();
        PlayCorrectedButton.IsEnabled = false;
        PlayCorrectedButton.Content = "▶ Play Corrected";

        _isRendering = true;
        RenderButton.IsEnabled = false;
        RenderProgress.Value = 0;
        RenderProgress.Visibility = Visibility.Visible;
        CorrectionStatus.Text = "Rendering...";

        float strength = (float)StrengthSlider.Value;

        var progress = new Progress<float>(v => Dispatcher.BeginInvoke(() =>
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
            _isRendering = false;
            RenderButton.IsEnabled = true;
        }

        if (outPath != null)
        {
            _correctedPath = outPath;
            RenderProgress.Visibility = Visibility.Collapsed;
            CorrectionStatus.Text = $"Saved: {Path.GetFileName(outPath)}";
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

    /// <summary>1px Bresenham line on a BGRA32 buffer (pitch contour only — no fills).</summary>
    private static void DrawLineBresenham(int[] pixels, int width, int height, int x0, int y0, int x1, int y1, int color)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                pixels[y0 * width + x0] = color;

            if (x0 == x1 && y0 == y1) break;

            int e2 = err * 2;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static int MidiToY(int midi, int height)
    {
        float norm = 1f - (float)(midi - MidiYMin) / MidiRange;
        return (int)(norm * (height - 1));
    }

    private static int BlendColour(int from, int to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int br = (byte)((from >> 16) & 0xFF), bg = (byte)((from >> 8) & 0xFF), bb = (byte)(from & 0xFF);
        int tr = (byte)((to >> 16) & 0xFF), tg = (byte)((to >> 8) & 0xFF), tb = (byte)(to & 0xFF);
        return (255 << 24) | ((int)(br + (tr - br) * t) << 16)
                           | ((int)(bg + (tg - bg) * t) << 8)
                           | (int)(bb + (tb - bb) * t);
    }

    private static int Bgra(byte r, byte g, byte b)
        => (255 << 24) | (r << 16) | (g << 8) | b;
}
