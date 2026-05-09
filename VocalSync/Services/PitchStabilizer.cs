namespace VocalSync.Services;

/// <summary>
/// Stateful post-processor that sits between raw pitch detection and the UI.
///
/// Responsibilities (in order of application):
///   1. Silence streak — suppress display after N consecutive silent frames.
///   2. Max-jump rejection — discard estimates that leap an impossible interval.
///   3. Exponential frequency smoothing — damps jitter in the Hz readout.
///   4. Note hysteresis — only commit a note change after it holds for N frames.
///
/// All thresholds are named constants so they are easy to tune later.
/// No allocations happen inside <see cref="Process"/> — all state is primitive fields.
/// </summary>
public class PitchStabilizer
{
    // ── Silence gate ───────────────────────────────────────────────────────

    /// <summary>
    /// Frames with no detected pitch before we blank the display.
    /// At ~20 fps (50 ms buffers) this is ~150 ms of grace before blanking.
    /// </summary>
    private const int SilenceFramesToBlank = 3;

    private int _silenceStreak;

    // ── Max-jump rejection ─────────────────────────────────────────────────

    /// <summary>
    /// Maximum ratio between consecutive non-zero frequency estimates
    /// that is considered plausible. A ratio above this means the new
    /// estimate jumped by more than ~2 semitones per frame and is rejected.
    ///
    /// 2 semitones = 2^(2/12) ≈ 1.122.  We use 1.20 to allow a little slack.
    /// </summary>
    private const float MaxJumpRatio = 1.20f;

    private float _lastAcceptedFrequency;

    // ── Exponential frequency smoothing ───────────────────────────────────

    /// <summary>
    /// Smoothing factor α for the exponential moving average applied to
    /// accepted frequency estimates.  Closer to 1 = faster response,
    /// closer to 0 = heavier smoothing.
    /// 0.25 gives ~4-frame settling time (~200 ms at 50 ms buffer intervals).
    /// </summary>
    private const float SmoothingAlpha = 0.25f;

    private float _smoothedFrequency;

    // ── Note hysteresis ────────────────────────────────────────────────────

    /// <summary>
    /// How many consecutive frames the same candidate note must be detected
    /// before the displayed note is updated. Prevents single-frame note flips.
    /// 3 frames ≈ 150 ms at 50 ms buffer intervals.
    /// </summary>
    private const int NoteHoldFrames = 3;

    private string _displayedNote = "--";
    private string _candidateNote = "--";
    private int _candidateHoldCount;

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Processes one raw detection result and returns a stable output.
    /// </summary>
    /// <param name="rawFrequency">
    ///   Raw frequency from <see cref="PitchDetectionService"/>.
    ///   0 means no pitch detected this frame.
    /// </param>
    /// <returns>
    ///   A <see cref="StabilizedPitch"/> with the smoothed frequency and
    ///   the committed note name, or blanked values if in a silence window.
    /// </returns>
    public StabilizedPitch Process(float rawFrequency)
    {
        bool hasPitch = rawFrequency > 0f;

        // 1. Silence streak tracking
        if (!hasPitch)
        {
            _silenceStreak++;
            if (_silenceStreak >= SilenceFramesToBlank)
            {
                // Reset smoothing state so we don't coast on stale values
                _smoothedFrequency = 0f;
                _lastAcceptedFrequency = 0f;
                _candidateNote = "--";
                _candidateHoldCount = 0;
                _displayedNote = "--";
            }
            return new StabilizedPitch(0f, _displayedNote);
        }

        _silenceStreak = 0;

        // 2. Max-jump rejection
        // If we have a previous estimate, check whether this one jumped too far.
        if (_lastAcceptedFrequency > 0f)
        {
            float ratio = rawFrequency > _lastAcceptedFrequency
                ? rawFrequency / _lastAcceptedFrequency
                : _lastAcceptedFrequency / rawFrequency;

            if (ratio > MaxJumpRatio)
            {
                // Reject this estimate — return the last stable state
                return new StabilizedPitch(_smoothedFrequency, _displayedNote);
            }
        }

        _lastAcceptedFrequency = rawFrequency;

        // 3. Exponential smoothing on the accepted frequency
        if (_smoothedFrequency <= 0f)
            _smoothedFrequency = rawFrequency; // cold start — snap immediately
        else
            _smoothedFrequency = SmoothingAlpha * rawFrequency + (1f - SmoothingAlpha) * _smoothedFrequency;

        // 4. Note hysteresis
        string candidateNote = NoteService.GetNoteName(rawFrequency);

        if (candidateNote == _candidateNote)
        {
            _candidateHoldCount++;
        }
        else
        {
            // New candidate — reset the hold counter
            _candidateNote = candidateNote;
            _candidateHoldCount = 1;
        }

        // Only commit the note once it has held for enough frames
        if (_candidateHoldCount >= NoteHoldFrames)
            _displayedNote = _candidateNote;

        return new StabilizedPitch(_smoothedFrequency, _displayedNote);
    }

    /// <summary>Resets all state — call when the user stops capture.</summary>
    public void Reset()
    {
        _silenceStreak = 0;
        _lastAcceptedFrequency = 0f;
        _smoothedFrequency = 0f;
        _displayedNote = "--";
        _candidateNote = "--";
        _candidateHoldCount = 0;
    }
}

/// <summary>
/// Output of <see cref="PitchStabilizer.Process"/>.
/// A lightweight value type — no heap allocation.
/// </summary>
public readonly struct StabilizedPitch
{
    /// <summary>Exponentially smoothed frequency in Hz. 0 means silence/blanked.</summary>
    public float FrequencyHz { get; }

    /// <summary>Hysteresis-committed note name, e.g. "A4". "--" when silent.</summary>
    public string NoteName { get; }

    public bool HasPitch => FrequencyHz > 0f;

    public StabilizedPitch(float frequencyHz, string noteName)
    {
        FrequencyHz = frequencyHz;
        NoteName = noteName;
    }
}
