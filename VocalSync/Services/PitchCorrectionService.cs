using VocalSync.Models;
using VocalSync.Services.Processing;
using VocalSync.Services.Processing.Processors;

namespace VocalSync.Services;

/// <summary>
/// Public facade for offline pitch correction.
///
/// Internally delegates to <see cref="OfflineProcessorPipeline"/> with a single
/// <see cref="PitchCorrectionProcessor"/>. This preserves the original public API
/// so <see cref="Views.AnalysisWindow"/> requires no changes.
///
/// To add a second processor after pitch correction (e.g. a future normaliser),
/// add it to the pipeline in <see cref="Correct"/> — the call site is unaffected.
/// </summary>
public sealed class PitchCorrectionService
{
    /// <summary>
    /// Derives the corrected output path. Delegates to the processor so path
    /// logic stays in one place.
    /// </summary>
    public static string GetCorrectedPath(string originalPath)
        => new PitchCorrectionProcessor().GetOutputPath(originalPath);

    /// <summary>
    /// Performs pitch correction and writes the corrected WAV file.
    /// Blocking — call via Task.Run.
    /// </summary>
    /// <param name="sourcePath">Path to the original WAV file (never modified).</param>
    /// <param name="points">Pitch timeline from OfflineAnalysisService.</param>
    /// <param name="strength">Correction strength [0, 1]. 1 = full snap to semitone.</param>
    /// <param name="progress">Optional 0–1 progress callback.</param>
    /// <param name="cancel">Optional cancellation token.</param>
    /// <returns>Path to the written corrected WAV file.</returns>
    public string Correct(
        string            sourcePath,
        PitchPoint[]      points,
        float             strength = 1.0f,
        IProgress<float>? progress = null,
        CancellationToken cancel   = default)
    {
        var context = new ProcessorContext
        {
            PitchTimeline = points,
            Strength      = strength
        };

        return new OfflineProcessorPipeline()
            .Add(new PitchCorrectionProcessor())
            .Run(sourcePath, context, progress, cancel);
    }
}
