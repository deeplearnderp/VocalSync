namespace VocalSync.Services;

/// <summary>
/// Performs simple autocorrelation-based pitch detection on a float PCM buffer.
/// Accuracy is intentionally basic — this is the foundation pass.
/// </summary>
public class PitchDetectionService
{
    private const int SampleRate = 44100;

    // Vocal range: ~80 Hz (E2) to ~1100 Hz (C6)
    private const float MinFrequency = 80f;
    private const float MaxFrequency = 1100f;

    private const int MinPeriod = (int)(SampleRate / MaxFrequency);
    private const int MaxPeriod = (int)(SampleRate / MinFrequency);

    // Minimum RMS level to bother detecting pitch (silence gate).
    // Raised from 0.01 → 0.02 to better reject quiet background noise.
    private const float SilenceThreshold = 0.02f;

    /// <summary>
    /// Detects the fundamental frequency from a mono float PCM buffer.
    /// Returns 0 if no clear pitch is found. Existing callers are unaffected.
    /// </summary>
    public float Detect(float[] buffer, int count)
    {
        var (frequency, _) = DetectWithConfidence(buffer, count);
        return frequency;
    }

    /// <summary>
    /// Detects pitch and returns the normalised autocorrelation confidence alongside
    /// the frequency. Used by the offline analysis pipeline.
    /// </summary>
    /// <returns>
    /// (Frequency, Confidence) where Frequency is Hz (0 = unvoiced) and
    /// Confidence is the normalised autocorrelation peak clamped to [0, 1].
    /// </returns>
    public (float Frequency, float Confidence) DetectWithConfidence(float[] buffer, int count)
    {
        if (count < MaxPeriod * 2)
            return (0f, 0f);

        // Gate: skip processing silence
        float rms = ComputeRms(buffer, count);
        if (rms < SilenceThreshold)
            return (0f, 0f);

        // Autocorrelation: find the lag with the highest correlation in vocal range
        float bestCorrelation = float.MinValue;
        int bestPeriod = -1;

        for (int period = MinPeriod; period <= MaxPeriod && period < count / 2; period++)
        {
            float correlation = 0f;
            int compareLength = count - period;

            for (int i = 0; i < compareLength; i++)
                correlation += buffer[i] * buffer[i + period];

            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestPeriod = period;
            }
        }

        if (bestPeriod <= 0)
            return (0f, 0f);

        // Normalize correlation — reject weak correlations (noisy/non-pitched audio)
        float normalizer = 0f;
        for (int i = 0; i < count; i++)
            normalizer += buffer[i] * buffer[i];

        if (normalizer < 1e-6f)
            return (0f, 0f);

        float normalizedCorrelation = bestCorrelation / normalizer;

        // Threshold: correlation must be reasonably strong.
        // Raised from 0.1 → 0.25 to reject weak / noisy pitch estimates.
        if (normalizedCorrelation < 0.25f)
            return (0f, 0f);

        return (SampleRate / (float)bestPeriod, Math.Clamp(normalizedCorrelation, 0f, 1f));
    }

    private static float ComputeRms(float[] buffer, int count)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
            sum += buffer[i] * buffer[i];
        return MathF.Sqrt(sum / count);
    }
}
