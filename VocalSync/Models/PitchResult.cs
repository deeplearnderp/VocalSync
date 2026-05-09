namespace VocalSync.Models;

/// <summary>
/// Holds the result of a single pitch detection pass.
/// </summary>
public class PitchResult
{
    /// <summary>Detected fundamental frequency in Hz. 0 means no pitch detected.</summary>
    public float FrequencyHz { get; init; }

    /// <summary>Nearest musical note name, e.g. "A4", "C#3".</summary>
    public string NoteName { get; init; } = "--";

    /// <summary>Whether a clear pitch was detected in this buffer.</summary>
    public bool HasPitch => FrequencyHz > 0f;
}
