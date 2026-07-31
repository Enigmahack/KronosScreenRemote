namespace KronosScreenRemote;

using NAudio.Midi;

// Enumerates the host's USB-MIDI ports (via NAudio/winmm) and locates the Kronos.
//
// A Kronos plugged into USB enumerates as a standard USB-MIDI class device named
// like "KRONOS" (Windows may prefix a disambiguating number for a second unit,
// e.g. "2- KRONOS"), so matching is a case-insensitive substring - never a fixed
// device index, satisfying "not hard-coded to a specific USB slot."
//
// IMPORTANT: input and output are SEPARATE winmm enumerations; the Kronos's input
// index is unrelated to its output index. Each direction is resolved independently
// by name.
static class KronosMidiDevices
{
    // Default device-name substring. Overridable from settings (UsbMidiDeviceName).
    public const string DefaultMatch = "KRONOS";

    // The resolved Kronos device name if BOTH an input and an output port match the
    // substring, else null. Full SysEx control needs OUT (send requests) and IN
    // (receive replies + the live stream), so both must be present.
    public static string? Find(string match = DefaultMatch)
    {
        if (string.IsNullOrWhiteSpace(match)) match = DefaultMatch;
        int inIdx  = FindInputIndex(match);
        int outIdx = FindOutputIndex(match);
        if (inIdx < 0 || outIdx < 0) return null;
        try { return MidiIn.DeviceInfo(inIdx).ProductName; }
        catch { return match; }
    }

    // First input device index whose product name contains match (case-insensitive), or -1.
    public static int FindInputIndex(string match)
    {
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            try
            {
                if (MidiIn.DeviceInfo(i).ProductName.Contains(match, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            catch { }
        }
        return -1;
    }

    // First output device index whose product name contains match (case-insensitive), or -1.
    public static int FindOutputIndex(string match)
    {
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            try
            {
                if (MidiOut.DeviceInfo(i).ProductName.Contains(match, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            catch { }
        }
        return -1;
    }

    // Lightweight, non-disruptive test that BOTH the Kronos input and output ports
    // can be opened right now. winmm ports are exclusive, so a device held by another
    // app (a DAW) enumerates as present but fails to open - this reveals that without
    // disturbing any live transport. Opens then immediately disposes each. Used by the
    // coordinator to retry a previously-failed USB open before committing to switch.
    public static bool CanOpen(string match = DefaultMatch)
    {
        if (string.IsNullOrWhiteSpace(match)) match = DefaultMatch;
        int inIdx  = FindInputIndex(match);
        int outIdx = FindOutputIndex(match);
        if (inIdx < 0 || outIdx < 0) return false;

        MidiIn?  mi = null;
        MidiOut? mo = null;
        try { mi = new MidiIn(inIdx); mo = new MidiOut(outIdx); return true; }
        catch { return false; }
        finally { try { mi?.Dispose(); } catch { } try { mo?.Dispose(); } catch { } }
    }

    public static IReadOnlyList<string> InputNames()
    {
        var list = new List<string>();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            try { list.Add(MidiIn.DeviceInfo(i).ProductName); } catch { }
        return list;
    }

    public static IReadOnlyList<string> OutputNames()
    {
        var list = new List<string>();
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            try { list.Add(MidiOut.DeviceInfo(i).ProductName); } catch { }
        return list;
    }
}
