namespace KronosScreenRemote;

// MIDI note number <-> name conversion (0-127) for display/entry in the Sample Editor's
// key-range fields. C4 = 60 (this app's own established convention throughout - see
// e.g. every "Original key (0-127, C4 = 60)" prompt); some DAWs use C3=60 or C5=60
// instead, but the Kronos's own onscreen note naming matches C4=60.
static class MidiNoteName
{
    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static string ToName(int midiNumber)
    {
        int clamped = Math.Clamp(midiNumber, 0, 127);
        int octave = clamped / 12 - 1;
        return $"{Names[clamped % 12]}{octave}";
    }

    // Parses "C4", "C#4", "Db4", "e1", "G#-1"... - letter, optional sharp/flat, then a
    // (possibly negative) octave number. Returns null if unparseable rather than
    // throwing, matching this codebase's Open()/TryParse conventions for "bad input is
    // expected user-editing state, not a bug."
    public static int? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length < 2) return null;

        int baseIndex = char.ToUpperInvariant(text[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1,
        };
        if (baseIndex < 0) return null;

        int i = 1;
        if (i < text.Length && (text[i] is '#' or 's' or 'S')) { baseIndex++; i++; }
        else if (i < text.Length && text[i] is 'b' or 'B') { baseIndex--; i++; }

        if (i >= text.Length || !int.TryParse(text[i..], out int octave)) return null;

        int midi = (octave + 1) * 12 + baseIndex;
        return midi is >= 0 and <= 127 ? midi : null;
    }
}
