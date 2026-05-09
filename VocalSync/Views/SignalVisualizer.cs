using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VocalSync.Views;

/// <summary>
/// Lightweight Canvas-derived control that renders two things:
///   1. A horizontal input-level bar (top strip).
///   2. A centre-line waveform drawn from a float[] sample snapshot (main area).
///
/// Rendering uses a <see cref="WriteableBitmap"/> written directly in software —
/// no DirectX, no SkiaSharp, no retained-mode geometry objects.
/// The bitmap is recreated only when the control is resized; per-frame updates
/// only overwrite pixel rows (no GC pressure beyond the already-allocated buffer).
///
/// Call <see cref="Update"/> from the WPF dispatcher thread (e.g. inside
/// Dispatcher.BeginInvoke) — it is NOT thread-safe.
/// </summary>
public sealed class SignalVisualizer : FrameworkElement
{
    // ── Colours (pre-multiplied BGRA32 int literals) ──────────────────────
    private static readonly int ColBackground  = ToBgra(0x12, 0x12, 0x12); // #121212
    private static readonly int ColWaveform    = ToBgra(0x1D, 0xB9, 0x54); // Spotify green
    private static readonly int ColWaveformDim = ToBgra(0x0D, 0x55, 0x28); // darker green for body fill
    private static readonly int ColMeterFill   = ToBgra(0x1D, 0xB9, 0x54);
    private static readonly int ColMeterClip   = ToBgra(0xE5, 0x53, 0x3D); // red above 90 %
    private static readonly int ColMeterBg     = ToBgra(0x1E, 0x1E, 0x1E);
    private static readonly int ColCentreLine  = ToBgra(0x2A, 0x2A, 0x2A);

    // ── Layout constants ──────────────────────────────────────────────────
    private const int MeterHeight    = 6;   // px — thin strip at top
    private const int MeterGap       = 6;   // px — gap between meter and waveform
    private const int MeterPaddingH  = 2;   // px — left/right inset of meter bar
    private const float ClipThreshold = 0.90f; // level above which meter turns red

    // ── Bitmap state ─────────────────────────────────────────────────────
    private WriteableBitmap? _bitmap;
    private int[]?           _pixels;   // reused pixel scratch buffer (BGRA32)
    private int              _bw, _bh;  // bitmap width/height

    // ── Current frame data ────────────────────────────────────────────────
    private float   _level;            // 0–1
    private float[] _waveform = [];    // downsampled snapshot

    // ── Public update API ─────────────────────────────────────────────────

    /// <summary>
    /// Updates the visualizer with a new level and waveform snapshot.
    /// Must be called on the UI (dispatcher) thread.
    /// </summary>
    /// <param name="level">Normalised input level, 0–1.</param>
    /// <param name="waveform">Downsampled waveform snapshot (any length).</param>
    public void Update(float level, float[] waveform)
    {
        _level    = Math.Clamp(level, 0f, 1f);
        _waveform = waveform;
        EnsureBitmap();
        Draw();
    }

    /// <summary>Blanks the visualizer (called on stop).</summary>
    public void Clear()
    {
        _level    = 0f;
        _waveform = [];
        EnsureBitmap();
        Draw();
    }

    // ── FrameworkElement overrides ────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
        => availableSize.Width == double.PositiveInfinity
            ? new Size(200, 80)
            : availableSize;

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Bitmap must be rebuilt whenever size changes
        if ((int)finalSize.Width  != _bw ||
            (int)finalSize.Height != _bh)
        {
            _bitmap = null; // force recreate
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureBitmap();
        if (_bitmap != null)
            dc.DrawImage(_bitmap, new Rect(0, 0, _bw, _bh));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _bitmap = null; // size changed — will be rebuilt on next Draw()
    }

    // ── Bitmap management ─────────────────────────────────────────────────

    private void EnsureBitmap()
    {
        int w = Math.Max(1, (int)ActualWidth);
        int h = Math.Max(1, (int)ActualHeight);

        if (_bitmap != null && _bw == w && _bh == h)
            return;

        _bw     = w;
        _bh     = h;
        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _pixels = new int[w * h];
    }

    // ── Drawing ───────────────────────────────────────────────────────────

    private void Draw()
    {
        if (_bitmap == null || _pixels == null) return;

        int w = _bw, h = _bh;

        // 1. Clear background
        Array.Fill(_pixels, ColBackground);

        // 2. Level meter strip (top MeterHeight rows)
        DrawMeter(w);

        // 3. Waveform area (below meter + gap)
        int waveTop = MeterHeight + MeterGap;
        int waveH   = Math.Max(1, h - waveTop);
        DrawWaveform(w, waveTop, waveH);

        // 4. Commit to WriteableBitmap
        _bitmap.Lock();
        try
        {
            _bitmap.WritePixels(
                new Int32Rect(0, 0, w, h),
                _pixels,
                w * 4,   // stride = width * 4 bytes (BGRA32)
                0);
        }
        finally
        {
            _bitmap.Unlock();
        }

        InvalidateVisual();
    }

    private void DrawMeter(int w)
    {
        // Background of the meter row
        int barLeft  = MeterPaddingH;
        int barRight = w - MeterPaddingH;
        int barWidth = barRight - barLeft;

        for (int row = 0; row < MeterHeight; row++)
        {
            int fillPixels = (int)(barWidth * _level);
            for (int col = barLeft; col < barRight; col++)
            {
                int idx = row * w + col;
                int relCol = col - barLeft;
                float relPos = barWidth > 0 ? (float)relCol / barWidth : 0f;

                if (relCol < fillPixels)
                    _pixels[idx] = relPos >= ClipThreshold ? ColMeterClip : ColMeterFill;
                else
                    _pixels[idx] = ColMeterBg;
            }
        }
    }

    private void DrawWaveform(int w, int waveTop, int waveH)
    {
        // Centre line
        int centreY = waveTop + waveH / 2;
        for (int col = 0; col < w; col++)
            _pixels[centreY * w + col] = ColCentreLine;

        if (_waveform.Length < 2) return;

        // Map each pixel column to a waveform sample
        int sampleCount = _waveform.Length;

        for (int col = 0; col < w; col++)
        {
            // Index into the waveform array (nearest-neighbour)
            int si = (int)((float)col / w * sampleCount);
            si = Math.Clamp(si, 0, sampleCount - 1);

            float sample = _waveform[si];
            float clamped = Math.Clamp(sample, -1f, 1f);

            // Pixel y for this sample
            int sampleY = centreY - (int)(clamped * (waveH / 2f - 2));
            sampleY = Math.Clamp(sampleY, waveTop, waveTop + waveH - 1);

            // Draw a vertical line from centre to sample (filled body)
            int yMin = Math.Min(centreY, sampleY);
            int yMax = Math.Max(centreY, sampleY);

            for (int row = yMin; row <= yMax; row++)
            {
                bool isEdge = (row == yMin || row == yMax);
                _pixels[row * w + col] = isEdge ? ColWaveform : ColWaveformDim;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Packs R,G,B into a BGRA32 int (A=255).</summary>
    private static int ToBgra(byte r, byte g, byte b)
        => (255 << 24) | (r << 16) | (g << 8) | b;
}
