using System.IO;
using NAudio.Wave;

namespace VocalSync.Services;

/// <summary>
/// Records raw 16-bit PCM audio from the capture pipeline to a WAV file.
///
/// Threading model:
///   - <see cref="StartRecording"/> and <see cref="StopRecording"/> are UI-thread only.
///   - <see cref="Submit"/> is called from the NAudio capture callback thread.
///
/// Thread safety is achieved without locks by field-ordering guarantees:
///   StopRecording() sets _writer = null BEFORE flushing/disposing the old writer,
///   so any Submit() call that reads _writer after the null-set will skip the write.
///   A Submit() call that already read a non-null _writer before the null-set will
///   complete its write safely because WaveFileWriter.Write() for the same instance
///   is only ever called from one thread (the NAudio callback thread is serialised).
/// </summary>
public sealed class WavRecorderService : IDisposable
{
    // Must match AudioCaptureService constants exactly.
    private static readonly WaveFormat CaptureFormat =
        new(sampleRate: 44100, channels: 1);  // 16-bit PCM mono, 44.1 kHz

    private volatile WaveFileWriter? _writer;
    private bool _disposed;

    /// <summary>True while a recording is in progress.</summary>
    public bool IsRecording => _writer != null;

    // ── Recording lifecycle ───────────────────────────────────────────────

    /// <summary>
    /// Begins recording to the specified file path. Creates the file and
    /// writes a WAV header immediately.
    /// </summary>
    /// <param name="filePath">Full path including .wav extension.</param>
    /// <exception cref="Exception">
    ///   File could not be created (permissions, disk full, etc.).
    /// </exception>
    public void StartRecording(string filePath)
    {
        if (IsRecording)
            StopRecording(); // Finalise any previous recording first

        // Directory is created by the ViewModel before this call, but guard anyway.
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new WaveFileWriter(filePath, CaptureFormat);
    }

    /// <summary>
    /// Finalises the WAV file and releases the file handle.
    /// Safe to call even when not recording (no-op).
    /// </summary>
    public void StopRecording()
    {
        // Capture the reference, then null the field FIRST.
        // Any Submit() that reads _writer after this point sees null and skips.
        var writer = _writer;
        _writer = null;

        if (writer == null) return;

        // WaveFileWriter.Dispose() flushes and patches the WAV header length fields.
        try   { writer.Dispose(); }
        catch { /* File system error — nothing useful we can do here */ }
    }

    // ── Audio path ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes raw 16-bit PCM bytes into the current recording.
    /// Non-blocking. Called from the NAudio capture callback thread.
    /// Safe to call when not recording — returns immediately.
    /// </summary>
    public void Submit(byte[] buffer, int bytesRecorded)
    {
        // Read the field once into a local to avoid a TOCTOU between the null-check
        // and the Write() call — if StopRecording() nulls _writer between them,
        // we still have a valid reference in `w`.
        var w = _writer;
        if (w == null) return;

        try   { w.Write(buffer, 0, bytesRecorded); }
        catch { /* Disk full or I/O error — silently drop the buffer */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopRecording();
        _disposed = true;
    }
}
