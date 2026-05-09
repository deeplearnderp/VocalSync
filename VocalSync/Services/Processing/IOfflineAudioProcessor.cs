namespace VocalSync.Services.Processing;

/// <summary>
/// Contract for all internal offline audio processors.
///
/// Design principles:
///   - Intentionally tiny. A processor does one thing: transform PCM.
///   - No base class, no abstract factory, no reflection.
///   - <see cref="ProcessorContext"/> carries all per-run inputs so this
///     interface never needs to change as new processors are added.
///   - Processors are stateless value-transformers. State lives in the
///     context or in the processor's own private fields set at construction.
///
/// This is NOT a plugin system:
///   - Processors are compiled into the assembly — no DLL loading.
///   - There is no registry or discovery mechanism.
///   - Adding a processor means adding a C# class and registering it
///     explicitly in the call site that builds the pipeline.
/// </summary>
public interface IOfflineAudioProcessor
{
    /// <summary>
    /// Human-readable name shown in status text and log messages.
    /// Example: "Pitch Correction", "Vocal Harmony", "Voice Clone"
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Derives the output file path for this processor's result.
    /// Each processor appends its own suffix so outputs never collide.
    /// Example: "recording.wav" → "recording_corrected.wav"
    /// </summary>
    string GetOutputPath(string sourcePath);

    /// <summary>
    /// Transforms the input PCM buffer and returns a new buffer.
    /// The returned array may be the same length as <paramref name="pcm"/>
    /// (pitch correction) or a different length (time-stretch, future use).
    ///
    /// Blocking — intended to be called via Task.Run from the UI thread.
    /// Must not interact with WPF objects.
    /// </summary>
    /// <param name="pcm">
    ///   Mono float PCM in [-1, 1] at 44100 Hz. Read-only — do not modify.
    /// </param>
    /// <param name="context">Analysis data and settings for this run.</param>
    /// <param name="progress">Optional 0–1 progress reporter.</param>
    /// <param name="cancel">Optional cancellation token.</param>
    /// <returns>Processed mono float PCM in [-1, 1].</returns>
    float[] Process(
        float[]             pcm,
        ProcessorContext    context,
        IProgress<float>?   progress = null,
        CancellationToken   cancel   = default);
}
