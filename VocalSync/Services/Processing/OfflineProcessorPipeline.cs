using NAudio.Wave;

namespace VocalSync.Services.Processing;

/// <summary>
/// Runs a sequential chain of <see cref="IOfflineAudioProcessor"/> instances
/// against a source WAV file and writes the result to disk.
///
/// Pipeline flow:
///   1. Read source WAV → float[] mono PCM.
///   2. Pass PCM through each processor in order (output feeds next input).
///   3. Write final float[] → 16-bit PCM WAV at the last processor's output path.
///
/// This is deliberately simple:
///   - No dynamic routing.
///   - No parallel branches.
///   - No feedback loops.
///   - No hot-swap or runtime registration.
///
/// Processors are added by the call site (e.g. AnalysisPanel or a future
/// RenderViewModel) — the pipeline has no awareness of which processors exist.
///
/// Progress reporting:
///   Each processor receives a sub-range of the overall 0–1 progress span,
///   divided evenly. A single-processor pipeline gives that processor the full
///   0–1 range; a two-processor pipeline gives each processor 0–0.5 and 0.5–1.
/// </summary>
public sealed class OfflineProcessorPipeline
{
    private const int SampleRate = 44100;
    private const int BitDepth   = 16;
    private const int Channels   = 1;

    private readonly List<IOfflineAudioProcessor> _processors = [];

    // ── Builder ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a processor to the end of the chain.
    /// Returns <c>this</c> for fluent chaining: pipeline.Add(a).Add(b).
    /// </summary>
    public OfflineProcessorPipeline Add(IOfflineAudioProcessor processor)
    {
        _processors.Add(processor);
        return this;
    }

    // ── Execution ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the pipeline against <paramref name="sourcePath"/> and writes
    /// the output of the final processor to disk.
    ///
    /// Blocking — call via Task.Run from the UI thread.
    /// </summary>
    /// <param name="sourcePath">Path to the original WAV file (never modified).</param>
    /// <param name="context">Shared analysis data and settings for this run.</param>
    /// <param name="progress">Optional overall 0–1 progress reporter.</param>
    /// <param name="cancel">Optional cancellation token.</param>
    /// <returns>
    ///   Path to the written output file, derived from the last processor's
    ///   <see cref="IOfflineAudioProcessor.GetOutputPath"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///   No processors have been added, or the source WAV cannot be read.
    /// </exception>
    public string Run(
        string             sourcePath,
        ProcessorContext   context,
        IProgress<float>?  progress = null,
        CancellationToken  cancel   = default)
    {
        if (_processors.Count == 0)
            throw new InvalidOperationException("No processors in pipeline.");

        // 1. Read source PCM once — shared as the initial input
        float[] pcm = ReadMonoPcm(sourcePath);
        if (pcm.Length == 0)
            throw new InvalidOperationException($"Could not read: {sourcePath}");

        // 2. Pass through each processor; output feeds next processor's input
        float sliceSize = 1f / _processors.Count;

        for (int i = 0; i < _processors.Count; i++)
        {
            if (cancel.IsCancellationRequested) break;

            float sliceStart = i * sliceSize;
            IOfflineAudioProcessor processor = _processors[i];

            // Map this processor's 0–1 progress into its slice of overall progress
            IProgress<float>? slicedProgress = progress == null ? null
                : new Progress<float>(v => progress.Report(sliceStart + v * sliceSize));

            pcm = processor.Process(pcm, context, slicedProgress, cancel);
        }

        // 3. Derive output path from the last processor and write the result
        string outPath = _processors[^1].GetOutputPath(sourcePath);
        WriteMonoPcm(pcm, outPath);

        progress?.Report(1f);
        return outPath;
    }

    // ── WAV I/O ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a WAV file and decodes it to mono float PCM [-1, 1].
    /// NAudio handles format conversion and stereo→mono mixing.
    /// </summary>
    private static float[] ReadMonoPcm(string filePath)
    {
        try
        {
            using var reader = new AudioFileReader(filePath);
            var mono   = reader.WaveFormat.Channels == 1
                ? (NAudio.Wave.ISampleProvider)reader
                : new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(reader);

            int total = (int)(reader.TotalTime.TotalSeconds * SampleRate) + SampleRate;
            var buf   = new float[total];
            int read  = mono.Read(buf, 0, buf.Length);
            if (read < buf.Length) Array.Resize(ref buf, read);
            return buf;
        }
        catch { return []; }
    }

    /// <summary>
    /// Converts float PCM to 16-bit signed PCM and writes a WAV file.
    /// </summary>
    private static void WriteMonoPcm(float[] pcm, string outPath)
    {
        var fmt = new WaveFormat(SampleRate, BitDepth, Channels);
        using var writer = new WaveFileWriter(outPath, fmt);

        var buf = new byte[2];
        foreach (float s in pcm)
        {
            short sample = (short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue);
            buf[0] = (byte)(sample & 0xFF);
            buf[1] = (byte)((sample >> 8) & 0xFF);
            writer.Write(buf, 0, 2);
        }
    }
}
