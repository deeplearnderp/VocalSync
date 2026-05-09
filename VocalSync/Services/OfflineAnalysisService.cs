using NAudio.Wave;
using VocalSync.Models;

namespace VocalSync.Services;

/// <summary>
/// Performs offline (non-realtime) pitch analysis on a WAV file.
///
/// Strategy:
///   - Reads the WAV with <see cref="AudioFileReader"/> (handles format conversion).
///   - Slides a fixed-size analysis window across the file with 50% hop overlap.
///   - Runs <see cref="PitchDetectionService.DetectWithConfidence"/> on each window.
///   - Applies the same stabilization used in realtime (jump rejection + smoothing)
///     via a fresh <see cref="PitchStabilizer"/> instance.
///   - Returns a <see cref="PitchPoint"/> array indexed by frame, each tagged with
///     time position, frequency, MIDI note, cents deviation, and confidence.
///
/// This service is stateless and allocation-bounded.
/// All heavy work runs on the caller's thread — invoke via Task.Run.
///
/// Architecture note for future correction passes:
///   <see cref="PitchPoint.MidiNote"/> and <see cref="PitchPoint.CentsOffset"/>
///   provide the data a pitch correction pass would need to compute a target
///   frequency and a per-frame shift amount. The Confidence field lets a
///   correction pass skip or weight-down unreliable frames.
/// </summary>
public sealed class OfflineAnalysisService
{
    private const int SampleRate = 44100;

    // Analysis window: 50 ms matches the realtime capture buffer size,
    // so the same detector thresholds apply without retuning.
    private const int WindowSamples = (int)(SampleRate * 0.050); // 2205

    // Hop size: 50% overlap gives ~20 ms resolution (40 fps equivalent).
    // Finer resolution → more frames → smoother graph, but longer analysis time.
    private const int HopSamples = WindowSamples / 2;            // 1102

    /// <summary>
    /// Analyses the WAV file at <paramref name="filePath"/> and returns a pitch
    /// timeline. Returns an empty array if the file cannot be read.
    ///
    /// This is a synchronous, blocking call. Run via Task.Run from the UI thread.
    /// </summary>
    public PitchPoint[] Analyse(string filePath, CancellationToken cancel = default)
    {
        float[] pcm = ReadMonoPcm(filePath);
        if (pcm.Length == 0) return [];

        var detector   = new PitchDetectionService();
        var stabilizer = new PitchStabilizer();
        var window     = new float[WindowSamples];
        var points     = new List<PitchPoint>();

        for (int offset = 0; offset + WindowSamples <= pcm.Length; offset += HopSamples)
        {
            if (cancel.IsCancellationRequested) break;

            // Copy window (avoids passing array slices — detector expects from index 0)
            Array.Copy(pcm, offset, window, 0, WindowSamples);

            var (rawFreq, confidence) = detector.DetectWithConfidence(window, WindowSamples);

            // Run through stabilizer to apply the same jump-rejection and smoothing
            // used in realtime. This reduces octave-error spikes in the graph.
            StabilizedPitch stable = stabilizer.Process(rawFreq);

            float displayFreq = stable.HasPitch ? stable.FrequencyHz : 0f;
            float timeSeconds = (float)offset / SampleRate;

            points.Add(BuildPoint(timeSeconds, displayFreq, confidence));
        }

        return [.. points];
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PitchPoint BuildPoint(float timeSeconds, float frequencyHz, float confidence)
    {
        if (frequencyHz <= 0f)
        {
            return new PitchPoint
            {
                TimeSeconds = timeSeconds,
                FrequencyHz = 0f,
                NoteName    = "--",
                MidiNote    = -1,
                CentsOffset = 0f,
                Confidence  = 0f
            };
        }

        // MIDI note: A4 = 440 Hz = MIDI 69
        double exactMidi   = 12.0 * Math.Log2(frequencyHz / 440.0) + 69.0;
        int    roundedMidi = (int)Math.Round(exactMidi);
        roundedMidi        = Math.Clamp(roundedMidi, 0, 127);

        // Cents deviation: how far the raw frequency sits from the nearest semitone
        float centsOffset = (float)((exactMidi - roundedMidi) * 100.0);

        int    octave   = (roundedMidi / 12) - 1;
        string[] names  = ["C","C#","D","D#","E","F","F#","G","G#","A","A#","B"];
        string noteName = $"{names[roundedMidi % 12]}{octave}";

        return new PitchPoint
        {
            TimeSeconds = timeSeconds,
            FrequencyHz = frequencyHz,
            NoteName    = noteName,
            MidiNote    = roundedMidi,
            CentsOffset = centsOffset,
            Confidence  = confidence
        };
    }

    /// <summary>
    /// Reads a WAV file and returns a mono float PCM array in [-1, 1].
    /// NAudio's <see cref="AudioFileReader"/> handles stereo→mono mixing and
    /// format conversion automatically via its SampleProvider chain.
    /// Returns empty array on any error.
    /// </summary>
    private static float[] ReadMonoPcm(string filePath)
    {
        try
        {
            using var reader   = new AudioFileReader(filePath);
            var mono     = reader.ToMono();         // ISampleProvider, 1 channel
            int       total    = (int)(reader.TotalTime.TotalSeconds * SampleRate) + SampleRate;
            var       buf      = new float[total];
            int       readBack = mono.Read(buf, 0, buf.Length);
            // Trim to actual read length
            if (readBack < buf.Length)
                Array.Resize(ref buf, readBack);
            return buf;
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// Extension: converts any ISampleProvider to mono by averaging channels.
/// Placed here to avoid a new file for a single short method.
/// </summary>
internal static class SampleProviderExtensions
{
    /// <summary>
    /// Returns the reader as a mono <see cref="ISampleProvider"/>.
    /// If already mono, returns the reader directly (no conversion).
    /// If stereo (or more), uses NAudio's built-in stereo-to-mono converter.
    /// </summary>
    internal static NAudio.Wave.ISampleProvider ToMono(this AudioFileReader reader)
    {
        if (reader.WaveFormat.Channels == 1)
            return reader;

        return new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(reader);
    }
}
