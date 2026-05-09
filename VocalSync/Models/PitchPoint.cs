namespace VocalSync.Models;

/// <summary>
/// Represents a single pitch estimate at a point in time from offline analysis.
///
/// Designed to support future correction passes:
/// - <see cref="FrequencyHz"/> is the raw detected fundamental.
/// - <see cref="MidiNote"/> is the nearest MIDI pitch (integer semitone).
/// - <see cref="CentsOffset"/> is the deviation from the nearest semitone in cents
///   (±50 range). Future pitch correction can target CentsOffset → 0.
/// - <see cref="Confidence"/> is the normalised autocorrelation peak [0, 1].
///   Future correction passes can use this to skip low-confidence frames.
/// </summary>
public sealed class PitchPoint
{
    /// <summary>Position in the recording in seconds.</summary>
    public float TimeSeconds { get; init; }

    /// <summary>Detected fundamental frequency in Hz. 0 = silence/unvoiced.</summary>
    public float FrequencyHz { get; init; }

    /// <summary>Nearest musical note name (e.g. "A4"). "--" when unvoiced.</summary>
    public string NoteName { get; init; } = "--";

    /// <summary>
    /// Nearest MIDI note number [0, 127]. -1 when unvoiced.
    /// Provides an integer pitch index for grid alignment and future correction.
    /// </summary>
    public int MidiNote { get; init; } = -1;

    /// <summary>
    /// Deviation from the nearest semitone in cents [-50, +50].
    /// 0 = perfectly in tune. Positive = sharp. Negative = flat.
    /// Reserved for future pitch correction; not displayed yet.
    /// </summary>
    public float CentsOffset { get; init; }

    /// <summary>
    /// Normalised autocorrelation confidence [0, 1].
    /// Higher values indicate a clearer, more reliable pitch estimate.
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>True when a voiced pitch was detected for this frame.</summary>
    public bool IsVoiced => FrequencyHz > 0f;
}
