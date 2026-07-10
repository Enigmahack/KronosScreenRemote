namespace KronosScreenRemote;

// Which backend carries the MIDI/SysEx traffic to the Kronos. The screen/video
// stream is always TCP (via the daemon); this selects only the MIDI path used by
// SysExService (mode/perf follow, Sync Names, Set List dump, note/CC send).
//   Auto — prefer a directly-connected Kronos USB-MIDI device; fall back to TCP.
//   Tcp  — always use the daemon (SYSEX / MIDI_SEND ctrl commands + port-9875 stream).
//   Usb  — always use direct USB-MIDI; unavailable when no Kronos USB device is present.
public enum MidiTransportMode
{
    Auto,
    Tcp,
    Usb,
}

// The concrete MIDI link currently carrying traffic — for the footer badge and the
// SysEx monitor's stream label. Distinct from MidiTransportMode (the user's
// preference): a USB-MIDI device whose name is NOT the Kronos's own port (lacks
// "KRONOS") is a generic 5-pin DIN interface bridging the Kronos, so it's shown as
// DIN (slow, DIN-rate) rather than USB (the Kronos's fast native USB port).
//   None — nothing active
//   Tcp  — the screenremote daemon over the network
//   Usb  — the Kronos's native USB-MIDI port
//   Din  — a generic USB-MIDI interface (5-pin DIN link to the Kronos)
public enum MidiLinkKind
{
    None,
    Tcp,
    Usb,
    Din,
}
