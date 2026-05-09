using VocalSync.Models;

namespace VocalSync.Services.Processing;

/// <summary>
/// Carries all per-run inputs that processors may need.
/// Passed by reference — processors read from it but never write to it.
///
/// Adding a new field here (e.g. TargetKey, VoiceProfile) does not change
/// <see cref="IOfflineAudioProcessor"/> — existing processors simply ignore
/// fields they don't use. This is the primary reason for the context bag:
/// the interface stays stable as new processor types are introduced.
///
/// Currently populated fields:
///   - <see cref="PitchTimeline"/>: frame-by-frame pitch analysis from OfflineAnalysisService.
///   - <see cref="SampleRate"/>: audio sample rate (always 44100 in this app).
///   - <see cref="Strength"/>: correction/effect strength [0, 1]. Not all processors use this.
///
/// Reserved for future processors (null/default until implemented):
///   - <see cref="TargetKey"/>: musical key for scale-aware correction (e.g. HarmonyProcessor).
///   - <see cref="VoiceProfilePath"/>: path to a voice model file (e.g. VoiceCloneProcessor).
/// </summary>
public sealed class ProcessorContext
{
    /// <summary>Sample rate of the source audio. Fixed at 44100 Hz in VocalSync.</summary>
    public int SampleRate { get; init; } = 44100;

    /// <summary>
    /// Frame-by-frame pitch analysis from <see cref="OfflineAnalysisService"/>.
    /// Required by pitch-aware processors. May be empty for processors that
    /// operate on raw PCM only (e.g. a future normaliser or noise gate).
    /// </summary>
    public PitchPoint[] PitchTimeline { get; init; } = [];

    /// <summary>
    /// Effect/correction strength in [0, 1].
    /// 0 = pass-through. 1 = full effect. Interpretation is processor-specific.
    /// </summary>
    public float Strength { get; init; } = 1.0f;

    // ── Reserved for future processors ───────────────────────────────────
    // These fields are intentionally null/default until a processor needs them.
    // Declaring them here now means future processors can read them without
    // any interface or context changes.

    /// <summary>
    /// Target musical key (e.g. "C", "F#"). Used by scale-aware processors
    /// such as a future HarmonyProcessor to snap to in-key notes only.
    /// Null = chromatic (snap to nearest semitone regardless of key).
    /// </summary>
    public string? TargetKey { get; init; } = null;

    /// <summary>
    /// Path to a voice model or reference audio file.
    /// Reserved for a future VoiceCloneProcessor or VocalSynthProcessor.
    /// Null = not used.
    /// </summary>
    public string? VoiceProfilePath { get; init; } = null;
}
