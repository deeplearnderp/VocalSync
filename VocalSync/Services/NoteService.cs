namespace VocalSync.Services;

/// <summary>
/// Converts a frequency (Hz) to the nearest musical note name.
/// Pure static utility — no state, no dependencies.
/// </summary>
public static class NoteService
{
    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>
    /// Returns the nearest note name for the given frequency.
    /// Returns "--" for frequencies outside a sensible vocal range.
    /// </summary>
    public static string GetNoteName(float frequencyHz)
    {
        if (frequencyHz <= 0f)
            return "--";

        // MIDI note number from frequency: A4 = 440 Hz = MIDI 69
        double midiNote = 12.0 * Math.Log2(frequencyHz / 440.0) + 69.0;
        int roundedMidi = (int)Math.Round(midiNote);

        if (roundedMidi < 0 || roundedMidi > 127)
            return "--";

        int octave = (roundedMidi / 12) - 1;
        string noteName = NoteNames[roundedMidi % 12];

        return $"{noteName}{octave}";
    }
}
