using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocalSync.Models;
using VocalSync.Services;
using VocalSync.ViewModels;

namespace VocalSync.Views;

/// <summary>
/// Embedded workspace panel: offline pitch graph, correction render, and corrected playback.
/// </summary>
public partial class AnalysisPanel : UserControl
{
    // ── MIDI bounds (absolute clamps — never used directly for scale) ─────
    private const int MidiAbsMin = 21;   // A0
    private const int MidiAbsMax = 108;  // C8
    private const int MidiMinSpan = 12;  // minimum 1 octave visible

    // ── Execution-order sequence counter (diagnostic only) ────────────────
    private static long _debugSeq;
    private static long NextSeq() => System.Threading.Interlocked.Increment(ref _debugSeq);
    private static int  Tid()     => System.Threading.Thread.CurrentThread.ManagedThreadId;
    private bool IsUiThread => Dispatcher.CheckAccess();

    // Compact thread-context tag: [T:1 UI:True]
    private string TC => $"[T:{Tid()} UI:{IsUiThread}]";

    // Single log helper — all diagnostic lines go through here
    private static void L(long seq, string msg)
        => Debug.WriteLine($"[VocalSync][SEQ {seq}] {msg}");

    // Callback id counter — each BeginInvoke gets a unique id
    private static long _cbSeq;
    private static long NextCb() => System.Threading.Interlocked.Increment(ref _cbSeq);

    // ── Source ownership watcher ──────────────────────────────────────────
    // Polls GraphImage.Source every 250ms after a commit is made.
    // Detects post-commit wipes, replacements, or unexpected clearing.
    private readonly DispatcherTimer _sourceWatcher;
    private ImageSource?  _lastKnownSource;   // reference from last commit or assignment
    private long          _lastKnownSourceSeq; // the SEQ at which we last set _lastKnownSource

    // ── Layout diagnostics ────────────────────────────────────────────────
    // Throttle LayoutUpdated: only log first N firings per analysis session.
    private int _layoutUpdateCount;
    private const int LayoutUpdateLogLimit = 8;

    /// <summary>
    /// Dumps ActualWidth/Height, RenderSize, DesiredSize, Visibility, IsVisible,
    /// HorizontalAlignment, VerticalAlignment for any FrameworkElement.
    /// </summary>
    private void DumpLayout(string name, FrameworkElement el)
    {
        L(NextSeq(),
            $"[Layout] {name} " +
            $"Actual={el.ActualWidth:F1}x{el.ActualHeight:F1} " +
            $"Render={el.RenderSize.Width:F1}x{el.RenderSize.Height:F1} " +
            $"Desired={el.DesiredSize.Width:F1}x{el.DesiredSize.Height:F1} " +
            $"Vis={el.Visibility} IsVis={el.IsVisible} " +
            $"H={el.HorizontalAlignment} V={el.VerticalAlignment} " +
            $"{TC}");
    }

    /// <summary>
    /// Walks the visual parent chain from <paramref name="start"/> upward,
    /// logging type, name, ActualWidth/Height, Visibility for each ancestor
    /// until the chain ends. Stops at 20 levels to avoid infinite loops.
    /// </summary>
    private void WalkParentChain(System.Windows.Media.Visual start)
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(start);
        int level   = 0;
        while (current != null && level < 20)
        {
            string typeName = current.GetType().Name;
            string elName   = (current is FrameworkElement fe) ? (fe.Name ?? "") : "";
            double aw = (current is FrameworkElement fe2) ? fe2.ActualWidth  : -1;
            double ah = (current is FrameworkElement fe3) ? fe3.ActualHeight : -1;
            Visibility vis = (current is UIElement ui) ? ui.Visibility : Visibility.Visible;
            L(NextSeq(),
                $"[ParentChain] L{level} {typeName}'{elName}' " +
                $"Actual={aw:F1}x{ah:F1} Vis={vis} {TC}");
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            level++;
        }
    }

    /// <summary>Dumps all row ActualHeight values from the named inner Grid (header/timeline/graph/legend).</summary>
    private void DumpGridRows(Grid grid, string gridName)
    {
        for (int r = 0; r < grid.RowDefinitions.Count; r++)
        {
            RowDefinition rd = grid.RowDefinitions[r];
            L(NextSeq(),
                $"[GridRow] {gridName} Row[{r}] " +
                $"Height={rd.Height} ActualHeight={rd.ActualHeight:F1} " +
                $"Min={rd.MinHeight} Max={rd.MaxHeight} {TC}");
        }
    }

    /// <summary>Logs GraphImage Source state and stretch settings.</summary>
    private void DumpImageState()
    {
        string srcInfo = GraphImage.Source == null
            ? "null"
            : $"{GraphImage.Source.GetType().Name} " +
              (GraphImage.Source is System.Windows.Media.Imaging.BitmapSource bs
                  ? $"{bs.PixelWidth}x{bs.PixelHeight}"
                  : "");
        L(NextSeq(),
            $"[ImageState] GraphImage Source={srcInfo} " +
            $"Stretch={GraphImage.Stretch} " +
            $"Actual={GraphImage.ActualWidth:F1}x{GraphImage.ActualHeight:F1} " +
            $"Desired={GraphImage.DesiredSize.Width:F1}x{GraphImage.DesiredSize.Height:F1} " +
            $"{TC}");
    }

    /// <summary>Returns a short human-readable description of an ImageSource for logging.</summary>
    private static string DescribeSource(ImageSource? src)
    {
        if (src == null) return "null";
        if (src is WriteableBitmap wb)
            return $"WriteableBitmap({wb.PixelWidth}x{wb.PixelHeight})";
        if (src is System.Windows.Media.Imaging.BitmapSource bs)
            return $"{bs.GetType().Name}({bs.PixelWidth}x{bs.PixelHeight})";
        return src.GetType().Name;
    }

    // ── Render palette ────────────────────────────────────────────────────
    private static readonly int ColBackground   = Bgra(0x11, 0x11, 0x11);
    private static readonly int ColBandShade    = Bgra(0x14, 0x14, 0x16); // alternating octave band
    private static readonly int ColGrid         = Bgra(0x20, 0x20, 0x20); // semitone lines
    private static readonly int ColGridSemi5    = Bgra(0x28, 0x28, 0x28); // 5th / notable semitone
    private static readonly int ColGridOctave   = Bgra(0x48, 0x48, 0x50); // octave lines — clearly visible
    private static readonly int ColContourCore  = Bgra(0x1D, 0xB9, 0x54); // contour: Spotify green
    private static readonly int ColContourEdge  = Bgra(0x10, 0x6A, 0x30); // contour edge: darker green
    private static readonly int ColContourLow   = Bgra(0x28, 0x78, 0x40); // low-confidence green

    private PitchPoint[] _points = [];
    private string _sourcePath = string.Empty;
    private readonly PitchCorrectionService _corrector = new();
    private readonly WavPlaybackService _player = new();

    private string? _correctedPath;
    private bool _isRendering;

    /// <summary>Invalidates in-flight graph retry chains when analysis clears or a new session loads.</summary>
    private int _graphRedrawToken;

    // ── Cached render range (set each time RenderGraph runs) ──────────────
    /// <summary>Dynamic MIDI min/max used for the most recent graph render. Needed by note-label overlay.</summary>
    private int _renderMidiMin = 36;
    private int _renderMidiMax = 84;
    /// <summary>Total duration in seconds of the last rendered clip (for timeline).</summary>
    private float _renderTotalSeconds = 0f;

    // ── Playhead timer ─────────────────────────────────────────────────────
    /// <summary>~30 fps ticker that drives the playback cursor overlay.</summary>
    private readonly DispatcherTimer _playheadTimer;

    /// <summary>Main window VM — used only for library WAV playhead sync (no binding).</summary>
    private MainViewModel? _workspacePlaybackVm;

    /// <summary>Matches main / settings output device for corrected playback.</summary>
    public void SetPlaybackDevice(int deviceNumber) => _player.SetDevice(deviceNumber);

    /// <summary>Wires library playback state from <see cref="MainViewModel"/> for graph playhead sync.</summary>
    public void AttachWorkspacePlayback(MainViewModel? vm) => _workspacePlaybackVm = vm;

    /// <summary>Called when <see cref="MainViewModel.IsPlayingBack"/> changes (main window PropertyChanged).</summary>
    public void OnMainWorkspacePlaybackStateChanged(bool isPlayingBack)
    {
        if (isPlayingBack)
            EnsurePlayheadTimerRunning();
        else
            StopPlayheadTimerIfIdle();
    }

    public AnalysisPanel()
    {
        InitializeComponent();

        StrengthSlider.ValueChanged += (_, e) =>
            StrengthLabel.Text = $"{e.NewValue:P0}";

        _player.PlaybackStopped += () => Dispatcher.BeginInvoke(() =>
        {
            PlayCorrectedButton.Content = "▶ Play Corrected";
            StopPlayheadTimerIfIdle();
        });

        // Playhead timer: ~30 fps during corrected WAV or library WAV playback.
        _playheadTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();

        Unloaded += (_, _) =>
        {
            _playheadTimer.Stop();
            _sourceWatcher.Stop();
            _player.Dispose();
        };

        // Bitmap width/height come from GraphContentGrid (stable); w≤1 would collapse x resolution.
        // GraphImage collapses to 0×0 when Source is null (WPF Image); resize redraw uses GraphContentGrid.
        GraphImage.SizeChanged += (_, args) =>
        {
            double cw = GraphContentGrid.ActualWidth;
            double ch = GraphContentGrid.ActualHeight;
            L(NextSeq(),
                $"GraphImage.SizeChanged img={GraphImage.ActualWidth:F0}x{GraphImage.ActualHeight:F0} " +
                $"grid={cw:F0}x{ch:F0} prevImg={args.PreviousSize.Width:F0}x{args.PreviousSize.Height:F0} " +
                $"_points={_points.Length} {TC}");
        };

        // Refresh note-label overlay on resize (canvas height changes with splitter)
        NoteLabelsCanvas.SizeChanged += (_, _) => DrawNoteLabels(_renderMidiMin, _renderMidiMax);

        // ── LAYOUT DIAGNOSTICS ────────────────────────────────────────────

        // 1. AnalysisPanel root — Loaded + SizeChanged
        Loaded += (_, _) =>
        {
            long s = NextSeq();
            L(s, $"[Layout] AnalysisPanel.Loaded {TC}");
            DumpLayout("AnalysisPanel(root)", this);
            DumpLayout("GraphImage", GraphImage);
        };

        SizeChanged += (_, args) =>
        {
            L(NextSeq(),
                $"[Layout] AnalysisPanel.SizeChanged " +
                $"new={ActualWidth:F1}x{ActualHeight:F1} " +
                $"prev={args.PreviousSize.Width:F1}x{args.PreviousSize.Height:F1} {TC}");
            DumpLayout("GraphImage@PanelSizeChanged", GraphImage);
        };

        // 2. GraphImage — Loaded + LayoutUpdated (throttled)
        GraphImage.Loaded += (_, _) =>
        {
            L(NextSeq(), $"[Layout] GraphImage.Loaded {TC}");
            DumpLayout("GraphImage", GraphImage);
            DumpLayout("GraphContentGrid", GraphContentGrid);
            DumpImageState();
            WalkParentChain(GraphImage);
        };

        GraphImage.LayoutUpdated += (_, _) =>
        {
            int cnt = System.Threading.Interlocked.Increment(ref _layoutUpdateCount);
            if (cnt > LayoutUpdateLogLimit) return;   // throttle after limit
            long s = NextSeq();
            L(s, $"[Layout] GraphImage.LayoutUpdated count={cnt}/{LayoutUpdateLogLimit} {TC}");
            DumpLayout("GraphImage", GraphImage);
            DumpImageState();
            if (cnt == LayoutUpdateLogLimit)
                L(NextSeq(), $"[Layout] LayoutUpdated throttle reached — suppressing further updates {TC}");
        };

        // 3. Graph outer Border (Grid.Row="2") — SizeChanged
        GraphBorder.SizeChanged += (_, args) =>
        {
            L(NextSeq(),
                $"[Layout] GraphBorder.SizeChanged " +
                $"new={GraphBorder.ActualWidth:F1}x{GraphBorder.ActualHeight:F1} " +
                $"prev={args.PreviousSize.Width:F1}x{args.PreviousSize.Height:F1} {TC}");
            DumpLayout("GraphImage@BorderSC", GraphImage);
        };

        // 4. Inner content grid (holds Image+Canvases) — SizeChanged (stable size when Image.Source is null)
        GraphContentGrid.SizeChanged += (_, args) =>
        {
            L(NextSeq(),
                $"[Layout] GraphContentGrid.SizeChanged " +
                $"new={GraphContentGrid.ActualWidth:F1}x{GraphContentGrid.ActualHeight:F1} " +
                $"prev={args.PreviousSize.Width:F1}x{args.PreviousSize.Height:F1} {TC}");
            double cw = GraphContentGrid.ActualWidth;
            double ch = GraphContentGrid.ActualHeight;
            if (_points.Length > 0 && cw >= 2 && ch >= 2)
                RenderGraph(_points);
        };

        // 5. Outer layout grid (header/timeline/graph/legend rows) — SizeChanged
        OuterLayoutGrid.SizeChanged += (_, args) =>
        {
            L(NextSeq(),
                $"[Layout] OuterLayoutGrid.SizeChanged " +
                $"new={OuterLayoutGrid.ActualWidth:F1}x{OuterLayoutGrid.ActualHeight:F1} " +
                $"prev={args.PreviousSize.Width:F1}x{args.PreviousSize.Height:F1} {TC}");
            DumpGridRows(OuterLayoutGrid, "OuterLayoutGrid");
        };

        UpdateCorrectionControls();

        // ── Source ownership watcher — 250ms poll (Phase 5) ──────────────
        // Fires whenever GraphImage.Source reference changes after any assignment.
        // Catches post-commit wipes, bindings overwriting, or unexpected null-sets.
        _sourceWatcher = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _sourceWatcher.Tick += (_, _) =>
        {
            ImageSource? current = GraphImage.Source;
            if (ReferenceEquals(current, _lastKnownSource)) return; // no change

            string prevDesc = DescribeSource(_lastKnownSource);
            string newDesc  = DescribeSource(current);
            long   changeSeq = NextSeq();
            L(changeSeq,
                $"[SourceWatcher] SOURCE CHANGED " +
                $"prev={prevDesc} -> now={newDesc} " +
                $"prevRegisteredAtSEQ={_lastKnownSourceSeq} {TC}");

            _lastKnownSource    = current;
            _lastKnownSourceSeq = changeSeq;
        };
        _sourceWatcher.Start();
    }

    /// <summary>Clears analysis UI (no file loaded).</summary>
    public void Clear()
    {
        long s = NextSeq();
        int  prevToken = _graphRedrawToken;
        int  prevLen   = _points.Length;

        _graphRedrawToken++;
        L(s, $"TOKEN {prevToken} -> {_graphRedrawToken} REASON:Clear {TC}");

        ResetCorrectionSession();

        L(NextSeq(), $"_points {prevLen} -> 0 REASON:Clear {TC}");
        _points = [];
        _sourcePath = string.Empty;
        _renderTotalSeconds = 0f;

        FileNameText.Text = "—";
        StatsText.Text    = "Select a recording in the library";
        VoicedBadge.Text  = "—";

        long clearSrcSeq = NextSeq();
        L(clearSrcSeq,
            $"[GraphSource] prev={DescribeSource(GraphImage.Source)} -> null REASON:Clear {TC}");
        GraphImage.Source = null;
        _lastKnownSource    = null;
        _lastKnownSourceSeq = clearSrcSeq;
        NoteLabelsCanvas.Children.Clear();
        TimelineCanvas.Children.Clear();

        ForceStopPlayhead();

        L(NextSeq(), $"Clear() EXIT token={_graphRedrawToken} _points=0 {TC}");
    }

    /// <summary>Loads a completed analysis session into the panel.</summary>
    public void SetAnalysis(PitchPoint[] points, string filePath)
    {
        long s = NextSeq();
        L(s, $"SetAnalysis() ENTRY points={points.Length} file={Path.GetFileName(filePath)} {TC}");
        // Reset layout update throttle so new analysis gets fresh layout diagnostics
        System.Threading.Interlocked.Exchange(ref _layoutUpdateCount, 0);

        ResetCorrectionSession();

        int prevToken = _graphRedrawToken;
        int prevLen   = _points.Length;
        _graphRedrawToken++;
        L(NextSeq(), $"TOKEN {prevToken} -> {_graphRedrawToken} REASON:SetAnalysis {TC}");

        _points = points;
        int voicedCount = points.Count(p => p.IsVoiced);
        PitchPoint? fv  = points.FirstOrDefault(p => p.IsVoiced);
        L(NextSeq(),
            $"_points {prevLen} -> {points.Length} voiced={voicedCount} REASON:SetAnalysis {TC} " +
            $"firstVoiced=(t={fv?.TimeSeconds:F3}s hz={fv?.FrequencyHz:F1} midi={fv?.MidiNote})");

        _sourcePath = filePath;

        PopulateHeader(points, filePath);
        ScheduleGraphRedraw();

        // Deferred layout snapshot: one dispatcher frame later, capture full layout state
        Dispatcher.BeginInvoke(() =>
        {
            L(NextSeq(), $"[Layout] POST-SetAnalysis snapshot {TC}");
            DumpLayout("AnalysisPanel", this);
            DumpLayout("OuterLayoutGrid", OuterLayoutGrid);
            DumpLayout("GraphBorder", GraphBorder);
            DumpLayout("GraphContentGrid", GraphContentGrid);
            DumpLayout("GraphImage", GraphImage);
            DumpGridRows(OuterLayoutGrid, "OuterLayoutGrid");
            DumpImageState();
            WalkParentChain(GraphImage);
        }, DispatcherPriority.ContextIdle);

        UpdateCorrectionControls();

        L(NextSeq(), $"SetAnalysis() EXIT token={_graphRedrawToken} _points={_points.Length} {TC}");
    }

    /// <summary>
    /// Waits until <see cref="GraphContentGrid"/> has a valid size, then draws <see cref="_points"/>.
    /// Retries on <see cref="DispatcherPriority.ContextIdle"/> (bounded) so we still redraw when
    /// <see cref="UIElement.SizeChanged"/> does not fire (same dimensions as previous selection).
    /// </summary>
    private void ScheduleGraphRedraw()
    {
        if (_points.Length == 0)
        {
            L(NextSeq(), $"ScheduleGraphRedraw() SKIPPED _points=0 token={_graphRedrawToken} {TC}");
            return;
        }

        int  token = _graphRedrawToken;
        long cbId  = NextCb();
        L(NextSeq(),
            $"ScheduleGraphRedraw() QUEUED cb#{cbId} token={token} _points={_points.Length} {TC}");

        void Step(int remainingRetries)
        {
            long ss = NextSeq();
            L(ss, $"Step() ENTERED cb#{cbId} retries={remainingRetries} token={token} " +
                  $"currentToken={_graphRedrawToken} _points={_points.Length} {TC}");

            // ── Exit A: token invalidated ──────────────────────────────────
            if (token != _graphRedrawToken)
            {
                L(NextSeq(),
                    $"Step() EXIT-A TOKEN-MISMATCH cb#{cbId} " +
                    $"captured={token} current={_graphRedrawToken} _points={_points.Length} {TC}");
                return;
            }

            // ── Exit B: points cleared ─────────────────────────────────────
            if (_points.Length == 0)
            {
                L(NextSeq(),
                    $"Step() EXIT-B POINTS-EMPTY cb#{cbId} " +
                    $"token={token} currentToken={_graphRedrawToken} {TC}");
                return;
            }

            // ── Exit C: graph content area not yet laid out (Image is 0×0 when Source is null) ──
            double gw = GraphContentGrid.ActualWidth;
            double gh = GraphContentGrid.ActualHeight;
            if (gw < 2 || gh < 2)
            {
                L(NextSeq(),
                    $"Step() EXIT-C SIZE-WAIT cb#{cbId} " +
                    $"GraphContentGrid={gw:F1}×{gh:F1} retries={remainingRetries} token={token} {TC}");
                if (remainingRetries > 0)
                {
                    long retryCbId = NextCb();
                    L(NextSeq(),
                        $"Step() REQUEUED cb#{retryCbId} (was cb#{cbId}) retries={remainingRetries - 1} {TC}");
                    Dispatcher.BeginInvoke(() => Step(remainingRetries - 1), DispatcherPriority.ContextIdle);
                }
                else
                {
                    L(NextSeq(),
                        $"Step() EXIT-D ABANDONED cb#{cbId} size never valid token={token} {TC}");
                }
                return;
            }

            // ── Exit E: success — fire render ─────────────────────────────
            L(NextSeq(),
                $"Step() EXIT-E FIRING cb#{cbId} _points={_points.Length} " +
                $"token={token} GraphContentGrid={gw:F0}×{gh:F0} " +
                $"GraphImage.Source={(GraphImage.Source == null ? "null" : "bmp")} {TC}");
            RenderGraph(_points);
        }

        // DispatcherPriority.Loaded: after layout pass, before Render
        Dispatcher.BeginInvoke(() => Step(4), DispatcherPriority.Loaded);
    }

    private void ResetCorrectionSession()
    {
        _isRendering = false;
        _correctedPath = null;
        StopPlayheadTimerIfIdle();
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
        long entrySeq = NextSeq();
        int  w = (int)GraphContentGrid.ActualWidth;
        int  h = (int)GraphContentGrid.ActualHeight;

        bool srcWasNull = GraphImage.Source == null;
        int  voicedCount = points.Count(p => p.IsVoiced);

        L(entrySeq,
            $"RenderGraph() ENTRY points={points.Length} voiced={voicedCount} " +
            $"bitmap={w}×{h} (from GraphContentGrid) token={_graphRedrawToken} " +
            $"GraphImage.Source={(srcWasNull ? "null" : "bmp")} {TC}");

        // Full layout state at render time
        DumpLayout("GraphImage@RenderEntry", GraphImage);
        DumpLayout("GraphBorder@RenderEntry", GraphBorder);
        DumpLayout("GraphContentGrid@RenderEntry", GraphContentGrid);
        DumpLayout("OuterLayoutGrid@RenderEntry", OuterLayoutGrid);
        DumpGridRows(OuterLayoutGrid, "OuterLayoutGrid@RenderEntry");

        if (w < 2 || h < 2)
        {
            L(NextSeq(), $"RenderGraph() EXIT-TINY bitmap={w}×{h} {TC}");
            return;
        }

        // ── 1. Compute dynamic MIDI range from voiced points ──────────────
        int midiMin, midiMax;
        var voiced = points.Where(p => p.IsVoiced && p.MidiNote >= MidiAbsMin && p.MidiNote <= MidiAbsMax).ToArray();

        if (voiced.Length > 0)
        {
            midiMin = voiced.Min(p => p.MidiNote);
            midiMax = voiced.Max(p => p.MidiNote);

            // Pad ±3 semitones so contour never sits at the very edge
            midiMin -= 3;
            midiMax += 3;

            // Snap BOTH bounds outward to the nearest C boundary
            // midiMin: floor down to the C at or below midiMin
            // midiMax: ceil up to the C at or above midiMax
            int cMin = (midiMin / 12) * 12;
            if (cMin > midiMin) cMin -= 12;          // ensure cMin <= midiMin
            int cMax = (midiMax / 12) * 12;
            if (cMax < midiMax) cMax += 12;          // ensure cMax >= midiMax

            midiMin = Math.Max(MidiAbsMin, cMin);
            midiMax = Math.Min(MidiAbsMax, cMax);

            // Guarantee at least MidiMinSpan (12) semitones visible
            if (midiMax - midiMin < MidiMinSpan)
            {
                int centre = (midiMin + midiMax) / 2;
                midiMin = Math.Max(MidiAbsMin, centre - MidiMinSpan / 2);
                midiMax = Math.Min(MidiAbsMax, midiMin + MidiMinSpan);
            }
        }
        else
        {
            // No voiced frames: show a sensible default vocal range
            midiMin = 48; // C3
            midiMax = 72; // C5
        }

        int midiRange = midiMax - midiMin;
        if (midiRange < 1) midiRange = 1; // unused — MidiToY computes range internally

        // Cache for note-label overlay and timeline
        _renderMidiMin = midiMin;
        _renderMidiMax = midiMax;
        _renderTotalSeconds = points.Length > 1 ? points[^1].TimeSeconds : 1f;
        if (_renderTotalSeconds <= 0f) _renderTotalSeconds = 1f;

        L(NextSeq(),
            $"RenderGraph() range midiMin={midiMin} midiMax={midiMax} " +
            $"voicedInRange={voiced.Length} totalTime={_renderTotalSeconds:F3}s {TC}");

        var pixels = new int[w * h];
        Array.Fill(pixels, ColBackground);

        // ── 2. Alternating octave band shading ────────────────────────────
        for (int octStart = (midiMin / 12) * 12; octStart <= midiMax; octStart += 12)
        {
            if ((octStart / 12) % 2 == 0)
            {
                int bandTop    = MidiToY(octStart + 11, h, midiMin, midiMax);
                int bandBottom = MidiToY(octStart,      h, midiMin, midiMax);
                bandTop    = Math.Clamp(bandTop,    0, h - 1);
                bandBottom = Math.Clamp(bandBottom, 0, h - 1);
                if (bandTop > bandBottom) (bandTop, bandBottom) = (bandBottom, bandTop);
                for (int y = bandTop; y <= bandBottom; y++)
                    for (int x = 0; x < w; x++)
                        pixels[y * w + x] = ColBandShade;
            }
        }

        // ── 3. Grid lines with hierarchy ──────────────────────────────────
        for (int midi = midiMin; midi <= midiMax; midi++)
        {
            int y = MidiToY(midi, h, midiMin, midiMax);
            if (y < 0 || y >= h) continue;

            int col;
            if (midi % 12 == 0)
                col = ColGridOctave;
            else if (midi % 12 == 5 || midi % 12 == 7)
                col = ColGridSemi5;
            else
                col = ColGrid;

            DrawLineBresenham(pixels, w, h, 0, y, w - 1, y, col);

            if (midi % 12 == 0 && y + 1 < h)
                DrawLineBresenham(pixels, w, h, 0, y + 1, w - 1, y + 1,
                    BlendColour(ColBackground, ColGridOctave, 0.35f));
        }

        // ── 4. Pre-contour data dump ──────────────────────────────────────
        {
            int totalPts  = points.Length;
            int voicedPts = points.Count(p => p.IsVoiced);
            float freqMin = voicedPts > 0 ? points.Where(p => p.IsVoiced).Min(p => p.FrequencyHz) : 0f;
            float freqMax = voicedPts > 0 ? points.Where(p => p.IsVoiced).Max(p => p.FrequencyHz) : 0f;
            int   mn2     = voicedPts > 0 ? points.Where(p => p.IsVoiced).Min(p => p.MidiNote) : -1;
            int   mx2     = voicedPts > 0 ? points.Where(p => p.IsVoiced).Max(p => p.MidiNote) : -1;

            L(NextSeq(),
                $"[GraphData] total={totalPts} voiced={voicedPts} " +
                $"freqHz=[{freqMin:F1},{freqMax:F1}] midiNoteRaw=[{mn2},{mx2}] " +
                $"graphRange=[{midiMin},{midiMax}] totalTime={_renderTotalSeconds:F3}s {TC}");

            for (int dbg = 0; dbg < Math.Min(10, points.Length); dbg++)
            {
                PitchPoint dp = points[dbg];
                L(NextSeq(),
                    $"  [pt{dbg:D2}] t={dp.TimeSeconds:F3}s " +
                    $"hz={dp.FrequencyHz:F1} midi={dp.MidiNote} " +
                    $"conf={dp.Confidence:F3} voiced={dp.IsVoiced}");
            }
        }

        // ── 5. Contour ────────────────────────────────────────────────────
        if (points.Length > 0)
            DrawContour(pixels, w, h, points, _renderTotalSeconds, midiMin, midiMax);

        // ── 6. Bitmap commit ──────────────────────────────────────────────
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.Lock();
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Unlock();

        // Pre-commit: full state snapshot
        long commitSeq = NextSeq();
        L(commitSeq,
            $"[GraphSource] PRE-COMMIT " +
            $"prev={DescribeSource(GraphImage.Source)} -> new=WriteableBitmap({w}x{h}) " +
            $"GraphContentGrid={GraphContentGrid.ActualWidth:F1}x{GraphContentGrid.ActualHeight:F1} " +
            $"GraphImage={GraphImage.ActualWidth:F1}x{GraphImage.ActualHeight:F1} " +
            $"IsLoaded={IsLoaded} Vis={Visibility} " +
            $"UIThread={IsUiThread} {TC}");

        GraphImage.Source = bmp;

        // Post-commit: verify Source reference survived the assignment
        bool committed = ReferenceEquals(GraphImage.Source, bmp);
        long postSeq = NextSeq();
        L(postSeq,
            $"[GraphSource] POST-COMMIT " +
            $"Source={DescribeSource(GraphImage.Source)} " +
            $"refEqualsBmp={committed} origin=RenderGraph {TC}");

        // Register with source watcher
        _lastKnownSource    = bmp;
        _lastKnownSourceSeq = commitSeq;

        // ── 7. Overlays ───────────────────────────────────────────────────
        DrawNoteLabels(midiMin, midiMax);
        DrawTimeline();

        L(NextSeq(), $"RenderGraph() EXIT-SUCCESS bitmap={w}×{h} token={_graphRedrawToken} {TC}");
    }

    /// <summary>
    /// Draws the pitch contour into the pixel buffer with maximum visibility.
    /// Uses solid bright-green 3×3 blocks and connecting lines — no blending.
    /// </summary>
    private static void DrawContour(int[] pixels, int w, int h,
        PitchPoint[] points, float totalTime,
        int midiMin, int midiMax)
    {
        // ── solid bright green — impossible to miss ───────────────────────
        const int ColGreen  = unchecked((int)0xFF00FF00);
        const int ColGreen2 = unchecked((int)0xFF00CC00);

        // ── ENTRY: scan incoming data ─────────────────────────────────────
        int entryVoiced = 0;
        int entryMidiLo = 999, entryMidiHi = -999;
        for (int ei = 0; ei < points.Length; ei++)
        {
            if (!points[ei].IsVoiced) continue;
            entryVoiced++;
            if (points[ei].MidiNote < entryMidiLo) entryMidiLo = points[ei].MidiNote;
            if (points[ei].MidiNote > entryMidiHi) entryMidiHi = points[ei].MidiNote;
        }
        L(NextSeq(),
            $"DrawContour() ENTRY points={points.Length} voiced={entryVoiced} " +
            $"midiInData=[{(entryVoiced > 0 ? entryMidiLo : -1)},{(entryVoiced > 0 ? entryMidiHi : -1)}] " +
            $"graphWindow=[{midiMin},{midiMax}] totalTime={totalTime:F3}");

        bool havePrev = false;
        int  prevPx   = 0, prevPy = 0;

        int cntTotal           = 0;
        int cntVoiced          = 0;
        int cntRejUnvoiced     = 0;
        int cntRejLow          = 0;
        int cntRejHigh         = 0;
        int cntRendered        = 0;
        int drawnSegments      = 0;
        int minX = w, maxX = 0, minY = h, maxY = 0;

        for (int i = 0; i < points.Length; i++)
        {
            PitchPoint pt = points[i];
            cntTotal++;

            if (pt.IsVoiced) cntVoiced++;

            if (!pt.IsVoiced)
            {
                cntRejUnvoiced++;
                havePrev = false;
                continue;
            }
            if (pt.MidiNote < midiMin)
            {
                cntRejLow++;
                havePrev = false;
                continue;
            }
            if (pt.MidiNote > midiMax)
            {
                cntRejHigh++;
                havePrev = false;
                continue;
            }

            cntRendered++;

            float tNorm = totalTime > 0f ? pt.TimeSeconds / totalTime : 0f;
            int px = Math.Clamp((int)(tNorm * (w - 1)), 0, w - 1);
            int py = MidiToY(pt.MidiNote, h, midiMin, midiMax);
            py = Math.Clamp(py, 0, h - 1);

            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;

            // ── 3×3 block at each voiced point ───────────────────────────
            for (int dy = -1; dy <= 1; dy++)
            {
                int ry = py + dy;
                if (ry < 0 || ry >= h) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int rx = px + dx;
                    if (rx >= 0 && rx < w)
                        pixels[ry * w + rx] = ColGreen;
                }
            }

            // ── connecting line from previous point ───────────────────────
            if (havePrev)
            {
                DrawLineBresenham(pixels, w, h, prevPx, prevPy,     px, py,     ColGreen);
                DrawLineBresenham(pixels, w, h, prevPx, prevPy + 1, px, py + 1, ColGreen2);
                drawnSegments++;
            }

            havePrev = true;
            prevPx   = px;
            prevPy   = py;
        }

        // ── SUMMARY ───────────────────────────────────────────────────────
        L(NextSeq(),
            $"DrawContour() EXIT total={cntTotal} voiced={cntVoiced} " +
            $"rendered={cntRendered} segments={drawnSegments} " +
            $"rejUnvoiced={cntRejUnvoiced} rejLow={cntRejLow} rejHigh={cntRejHigh} " +
            $"midiRange=[{midiMin},{midiMax}] bitmap={w}×{h} " +
            $"xRange=[{(minX<=maxX?minX:-1)},{(minX<=maxX?maxX:-1)}] " +
            $"yRange=[{(minY<=maxY?minY:-1)},{(minY<=maxY?maxY:-1)}]");
    }

    // ── Note-label overlay ────────────────────────────────────────────────

    /// <summary>
    /// Populates NoteLabelsCanvas with small "C3", "C4" text blocks
    /// at each octave boundary within the current render MIDI range.
    /// Called after every graph render and after canvas resize.
    /// </summary>
    private void DrawNoteLabels(int midiMin, int midiMax)
    {
        NoteLabelsCanvas.Children.Clear();

        double h = NoteLabelsCanvas.ActualHeight;
        if (h < 4) return;

        for (int midi = (midiMin / 12) * 12; midi <= midiMax; midi += 12)
        {
            if (midi < midiMin) continue;

            int octave = (midi / 12) - 1;
            string label = $"C{octave}";

            double y = MidiToY(midi, (int)h, midiMin, midiMax);

            var tb = new TextBlock
            {
                Text       = label,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0x88, 0x99, 0xAA)),
            };

            Canvas.SetLeft(tb, 3);
            Canvas.SetTop(tb,  y - 9); // sit just above the octave line
            NoteLabelsCanvas.Children.Add(tb);
        }
    }

    // ── Timeline drawing ──────────────────────────────────────────────────

    /// <summary>
    /// Redraws TimelineCanvas with major (second) and minor (sub-second) ticks
    /// based on the current clip duration. Called after render and canvas resize.
    /// </summary>
    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();

        double cw = TimelineCanvas.ActualWidth;
        double ch = TimelineCanvas.ActualHeight;
        if (cw < 4 || ch < 2 || _renderTotalSeconds <= 0f) return;

        // Bottom separator line
        var sep = new System.Windows.Shapes.Rectangle
        {
            Width  = cw,
            Height = 1,
            Fill   = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A)),
        };
        Canvas.SetLeft(sep, 0);
        Canvas.SetTop(sep, ch - 1);
        TimelineCanvas.Children.Add(sep);

        // Choose tick interval based on duration
        float dur = _renderTotalSeconds;
        double minorInterval = dur <= 5f  ? 0.25  :
                               dur <= 15f ? 0.5   :
                               dur <= 30f ? 1.0   : 2.0;
        double majorInterval = minorInterval * 4;

        double majorH = ch * 0.65;
        double minorH = ch * 0.30;

        var majorBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x58, 0x58, 0x60));
        var minorBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x34, 0x34, 0x38));
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x66, 0x66, 0x70));

        double t = 0;
        while (t <= dur + 1e-4)
        {
            double x = (t / dur) * cw;
            bool isMajor = Math.Abs(t % majorInterval) < 1e-4 ||
                           Math.Abs((t % majorInterval) - majorInterval) < 1e-4;

            double tickH = isMajor ? majorH : minorH;
            var brush    = isMajor ? majorBrush : minorBrush;

            var tick = new System.Windows.Shapes.Rectangle
            {
                Width  = 1,
                Height = tickH,
                Fill   = brush,
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, ch - tickH - 1);
            TimelineCanvas.Children.Add(tick);

            // Label major ticks with seconds (skip 0s label)
            if (isMajor && t > 0.5 && x < cw - 12)
            {
                string text = t >= 60
                    ? $"{(int)(t / 60)}:{(int)(t % 60):D2}"
                    : $"{t:0.#}s";

                var tb = new TextBlock
                {
                    Text       = text,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize   = 8,
                    Foreground = labelBrush,
                };
                Canvas.SetLeft(tb, x + 2);
                Canvas.SetTop(tb, 1);
                TimelineCanvas.Children.Add(tb);
            }

            t += minorInterval;
        }
    }

    /// <summary>Called by XAML when TimelineCanvas resizes (splitter drag, window resize).</summary>
    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => DrawTimeline();

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
            StopPlayheadTimerIfIdle();

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
        StopPlayheadTimerIfIdle();
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
            StopPlayheadTimerIfIdle();
            return;
        }

        try
        {
            _player.Play(_correctedPath);
            PlayCorrectedButton.Content = "■ Stop";
            EnsurePlayheadTimerRunning();
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

    // ── Playhead (DAW-style overlay — independent of graph bitmap redraw) ──

    private void EnsurePlayheadTimerRunning()
    {
        PlayheadCanvas.Visibility = Visibility.Visible;
        if (!_playheadTimer.IsEnabled)
            _playheadTimer.Start();
        UpdatePlayhead();
    }

    /// <summary>Stops the playhead timer when neither corrected nor library playback is active.</summary>
    private void StopPlayheadTimerIfIdle()
    {
        if (_player.IsPlaying) return;
        if (IsMainWorkspacePlaybackActive()) return;

        _playheadTimer.Stop();
        PlayheadCanvas.Visibility = Visibility.Collapsed;
        ResetPlayheadVisualToStart();
    }

    /// <summary>Workspace cleared — always hide playhead (no graph session).</summary>
    private void ForceStopPlayhead()
    {
        _playheadTimer.Stop();
        PlayheadCanvas.Visibility = Visibility.Collapsed;
        ResetPlayheadVisualToStart();
    }

    private bool IsMainWorkspacePlaybackActive()
    {
        return _workspacePlaybackVm != null
            && _workspacePlaybackVm.IsPlayingBack
            && _workspacePlaybackVm.MainPlaybackDuration.TotalSeconds > 0;
    }

    private void ResetPlayheadVisualToStart()
    {
        double h = GraphContentGrid.ActualHeight >= 2 ? GraphContentGrid.ActualHeight : 1;
        Canvas.SetLeft(PlayheadPlayedShade, 0);
        Canvas.SetTop(PlayheadPlayedShade, 0);
        PlayheadPlayedShade.Width  = 0;
        PlayheadPlayedShade.Height = h;

        PlayheadLine.X1 = PlayheadLine.X2 = 0;
        PlayheadLine.Y1 = 0;
        PlayheadLine.Y2 = h;
        PlayheadGlow.X1 = PlayheadGlow.X2 = 0;
        PlayheadGlow.Y1 = 0;
        PlayheadGlow.Y2 = h;

        HideActivePitchMarker();
    }

    private void UpdatePlayhead()
    {
        if (!_player.IsPlaying && !IsMainWorkspacePlaybackActive())
        {
            StopPlayheadTimerIfIdle();
            return;
        }

        double graphW = GraphContentGrid.ActualWidth;
        double graphH = GraphContentGrid.ActualHeight;
        if (graphW < 2 || graphH < 2)
        {
            HideActivePitchMarker();
            return;
        }

        TimeSpan current;
        TimeSpan total;
        if (_player.IsPlaying)
        {
            current = _player.CurrentTime;
            total   = _player.TotalTime;
        }
        else
        {
            current = _workspacePlaybackVm!.MainPlaybackPosition;
            total   = _workspacePlaybackVm.MainPlaybackDuration;
        }

        if (total.TotalSeconds <= 0)
        {
            HideActivePitchMarker();
            return;
        }

        double norm = Math.Clamp(current.TotalSeconds / total.TotalSeconds, 0.0, 1.0);
        // Same horizontal mapping as contour: tNorm * (w - 1)
        double x = Math.Round(norm * (graphW - 1));

        PlayheadPlayedShade.Width  = Math.Max(0, x);
        PlayheadPlayedShade.Height = graphH;
        Canvas.SetLeft(PlayheadPlayedShade, 0);
        Canvas.SetTop(PlayheadPlayedShade, 0);

        PlayheadLine.X1 = PlayheadLine.X2 = x;
        PlayheadLine.Y1 = 0;
        PlayheadLine.Y2 = graphH;

        PlayheadGlow.X1 = PlayheadGlow.X2 = x;
        PlayheadGlow.Y1 = 0;
        PlayheadGlow.Y2 = graphH;

        UpdateActivePitchHighlight(x, graphH, (float)current.TotalSeconds);
    }

    /// <summary>Positions active-pitch marker at playhead X; Y from nearest voiced frame (overlay only).</summary>
    private void UpdateActivePitchHighlight(double playheadX, double graphH, float timeSeconds)
    {
        if (_points.Length == 0 || graphH < 2)
        {
            HideActivePitchMarker();
            return;
        }

        int h = Math.Max(2, (int)Math.Round(graphH));
        if (!TryGetActiveVoicedPointNearTime(timeSeconds, h, out int py))
        {
            HideActivePitchMarker();
            return;
        }

        double cy = py + 0.5;

        const double glowSize = 16;
        Canvas.SetLeft(ActivePitchGlow, playheadX - glowSize * 0.5);
        Canvas.SetTop(ActivePitchGlow, cy - glowSize * 0.5);
        ActivePitchGlow.Visibility = Visibility.Visible;

        const double dotSize = 7;
        Canvas.SetLeft(ActivePitchDot, playheadX - dotSize * 0.5);
        Canvas.SetTop(ActivePitchDot, cy - dotSize * 0.5);
        ActivePitchDot.Visibility = Visibility.Visible;
    }

    private void HideActivePitchMarker()
    {
        ActivePitchGlow.Visibility  = Visibility.Collapsed;
        ActivePitchDot.Visibility   = Visibility.Collapsed;
    }

    /// <summary>Nearest voiced frame in a small window around <paramref name="timeSeconds"/> that lies in the current graph MIDI window.</summary>
    private bool TryGetActiveVoicedPointNearTime(float timeSeconds, int h, out int py)
    {
        py = 0;
        if (_points.Length == 0) return false;

        int c = FindClosestPointIndexByTime(_points, timeSeconds);
        if (c < 0) return false;

        int lo = Math.Max(0, c - 12);
        int hi = Math.Min(_points.Length - 1, c + 12);
        int bestI = -1;
        float bestDt = float.MaxValue;
        for (int i = lo; i <= hi; i++)
        {
            PitchPoint p = _points[i];
            if (!p.IsVoiced) continue;
            if (p.MidiNote < _renderMidiMin || p.MidiNote > _renderMidiMax) continue;
            if (p.MidiNote < MidiAbsMin || p.MidiNote > MidiAbsMax) continue;
            float dt = Math.Abs(p.TimeSeconds - timeSeconds);
            if (dt < bestDt)
            {
                bestDt = dt;
                bestI = i;
            }
        }

        if (bestI < 0) return false;

        py = Math.Clamp(MidiToY(_points[bestI].MidiNote, h, _renderMidiMin, _renderMidiMax), 0, h - 1);
        return true;
    }

    /// <summary>Index of analysis frame closest in time to <paramref name="t"/> (assumes non-decreasing <see cref="PitchPoint.TimeSeconds"/>).</summary>
    private static int FindClosestPointIndexByTime(PitchPoint[] points, float t)
    {
        int n = points.Length;
        if (n == 0) return -1;

        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (points[mid].TimeSeconds < t) lo = mid + 1;
            else hi = mid - 1;
        }

        if (lo <= 0) return 0;
        if (lo >= n) return n - 1;

        int prev = lo - 1;
        float dPrev = t - points[prev].TimeSeconds;
        float dNext = points[lo].TimeSeconds - t;
        return dPrev <= dNext ? prev : lo;
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

    private static int MidiToY(int midi, int height, int midiMin, int midiMax)
    {
        int range = midiMax - midiMin;
        if (range < 1) range = 1;
        float norm = 1f - (float)(midi - midiMin) / range;
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
