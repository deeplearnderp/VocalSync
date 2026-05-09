using System.IO;

namespace VocalSync.Models;

/// <summary>
/// One row in the in-memory recording library (scanned from /Recordings).
/// </summary>
public sealed class RecordingEntry
{
    public required string FilePath { get; init; }

    /// <summary>Original recording vs exported corrected WAV (<c>*_corrected.wav</c>).</summary>
    public required string SourceKind { get; init; }

    /// <summary>Best-effort timestamp (file time or parsed from VocalSync filename).</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>WAV length if readable; otherwise null.</summary>
    public TimeSpan? Duration { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public string TimestampLocalDisplay =>
        TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string DurationDisplay =>
        Duration is { TotalSeconds: > 0 } d
            ? d.TotalHours >= 1
                ? d.ToString(@"h\:mm\:ss")
                : d.ToString(@"m\:ss")
            : "—";

    public string SummaryLine =>
        $"{TimestampLocalDisplay}  ·  {SourceKind}  ·  {DurationDisplay}";
}
