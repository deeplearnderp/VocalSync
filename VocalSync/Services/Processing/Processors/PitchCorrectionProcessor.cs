using System.IO;
using VocalSync.Models;

namespace VocalSync.Services.Processing.Processors;

/// <summary>
/// Offline pitch correction processor: snaps each voiced frame toward the
/// nearest semitone using OLA (Overlap-Add) resampling.
///
/// DSP is unchanged from the original PitchCorrectionService — only the
/// structural placement has changed. File I/O has moved to
/// <see cref="OfflineProcessorPipeline"/>; this class only transforms float[].
///
/// Reads from <see cref="ProcessorContext"/>:
///   - <see cref="ProcessorContext.PitchTimeline"/> — required; empty = pass-through.
///   - <see cref="ProcessorContext.Strength"/>       — correction amount [0, 1].
///
/// Ignores: TargetKey, VoiceProfilePath (reserved for future processors).
/// </summary>
public sealed class PitchCorrectionProcessor : IOfflineAudioProcessor
{
    private const int SampleRate = 44100;

    // Analysis hop must match OfflineAnalysisService exactly so frame indices align.
    private const int WindowSamples = (int)(SampleRate * 0.050); // 2205
    private const int HopSamples    = WindowSamples / 2;          // 1102

    private const float ConfidenceGate = 0.30f;
    private const float MaxRatio       = 4.0f;
    private const float MinRatio       = 0.25f;
    private const int   CrossfadeSamples = 128;

    // ── IOfflineAudioProcessor ────────────────────────────────────────────

    public string Name => "Pitch Correction";

    /// <summary>
    /// Output path: inserts "_corrected" before the extension.
    /// "VocalSync_20250101_120000.wav" → "VocalSync_20250101_120000_corrected.wav"
    /// </summary>
    public string GetOutputPath(string sourcePath)
    {
        string dir  = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(dir, $"{stem}_corrected.wav");
    }

    /// <summary>
    /// Transforms the PCM buffer by shifting each voiced frame toward its
    /// nearest semitone. Returns a new float[] of the same length.
    /// Unvoiced and low-confidence frames are copied unmodified.
    /// </summary>
    public float[] Process(
        float[]            pcm,
        ProcessorContext   context,
        IProgress<float>?  progress = null,
        CancellationToken  cancel   = default)
    {
        float strength = Math.Clamp(context.Strength, 0f, 1f);
        PitchPoint[] points = context.PitchTimeline;

        float[] dst     = new float[pcm.Length];
        float[] fadeIn  = MakeFade(CrossfadeSamples, rising: true);
        float[] fadeOut = MakeFade(CrossfadeSamples, rising: false);

        int totalFrames = points.Length;

        for (int fi = 0; fi < totalFrames; fi++)
        {
            if (cancel.IsCancellationRequested) break;

            progress?.Report((float)fi / Math.Max(totalFrames, 1));

            int srcOffset = fi * HopSamples;
            int dstOffset = srcOffset;

            PitchPoint pt = points[fi];

            if (!pt.IsVoiced || pt.Confidence < ConfidenceGate || strength == 0f)
            {
                CopyFrame(pcm, srcOffset, dst, dstOffset, HopSamples, fadeIn, fadeOut);
                continue;
            }

            float targetHz      = MidiToHz(pt.MidiNote);
            float blendedTarget = pt.FrequencyHz + (targetHz - pt.FrequencyHz) * strength;
            float ratio         = Math.Clamp(blendedTarget / pt.FrequencyHz, MinRatio, MaxRatio);

            int readLen = Math.Clamp((int)(HopSamples / ratio), 1, pcm.Length - srcOffset);

            float[] shifted = LinearResample(pcm, srcOffset, readLen, HopSamples);
            OlaWrite(shifted, dst, dstOffset, HopSamples, fadeIn, fadeOut);
        }

        return dst;
    }

    // ── DSP helpers (identical to original PitchCorrectionService) ────────

    private static float[] LinearResample(float[] src, int srcOffset, int srcLen, int dstLen)
    {
        var   dst  = new float[dstLen];
        float step = (float)(srcLen - 1) / Math.Max(dstLen - 1, 1);

        for (int i = 0; i < dstLen; i++)
        {
            float pos  = i * step;
            int   lo   = (int)pos;
            int   hi   = Math.Min(lo + 1, srcLen - 1);
            float frac = pos - lo;
            int   loI  = Math.Clamp(srcOffset + lo, 0, src.Length - 1);
            int   hiI  = Math.Clamp(srcOffset + hi, 0, src.Length - 1);
            dst[i] = src[loI] + (src[hiI] - src[loI]) * frac;
        }

        return dst;
    }

    private static void OlaWrite(float[] frame, float[] dst, int dstOffset, int len,
                                  float[] fadeIn, float[] fadeOut)
    {
        int cfLen = Math.Min(CrossfadeSamples, len / 2);
        for (int i = 0; i < len; i++)
        {
            int idx = dstOffset + i;
            if (idx < 0 || idx >= dst.Length) continue;

            float w = 1f;
            if (i < cfLen)        w = fadeIn [i * CrossfadeSamples / Math.Max(cfLen, 1)];
            if (i >= len - cfLen) w = fadeOut[(i - (len - cfLen)) * CrossfadeSamples / Math.Max(cfLen, 1)];

            dst[idx] = Math.Clamp(dst[idx] * (1f - w) + frame[i] * w, -1f, 1f);
        }
    }

    private static void CopyFrame(float[] src, int srcOff, float[] dst, int dstOff,
                                   int len, float[] fadeIn, float[] fadeOut)
    {
        int cfLen = Math.Min(CrossfadeSamples, len / 2);
        for (int i = 0; i < len; i++)
        {
            int si = srcOff + i;
            int di = dstOff + i;
            if (si >= src.Length || di >= dst.Length) break;

            float w = 1f;
            if (i < cfLen)        w = fadeIn [i * CrossfadeSamples / Math.Max(cfLen, 1)];
            if (i >= len - cfLen) w = fadeOut[(i - (len - cfLen)) * CrossfadeSamples / Math.Max(cfLen, 1)];

            dst[di] = Math.Clamp(dst[di] * (1f - w) + src[si] * w, -1f, 1f);
        }
    }

    private static float[] MakeFade(int n, bool rising)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
            w[i] = rising ? (float)i / (n - 1) : 1f - (float)i / (n - 1);
        return w;
    }

    private static float MidiToHz(int midi)
        => 440f * MathF.Pow(2f, (midi - 69) / 12f);
}
