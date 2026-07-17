namespace KronosScreenRemote;

// Byte-level MIDI I/O abstraction to the Kronos. Two backends implement it:
//   • TcpMidiTransport — through the screenremote daemon: SYSEX / MIDI_SEND ctrl
//     commands for round-trip + fire-and-forget, and the port-9875 MIDI-out
//     firehose for the live inbound stream.
//   • UsbMidiTransport — direct USB-MIDI to the Kronos (NAudio/winmm), no daemon.
//
// SysExService and SysExDumpCollector are written entirely against this interface,
// so every SysEx feature (mode/perf follow, Sync Names, Set List dump, note/CC
// send) works identically over either backend. The Kronos protocol itself
// (request building, reply parsing, bank tables) lives above this layer in
// SysExService / KronosSysEx; the transport only moves bytes and correlates a
// reply to its request.
interface IKronosMidiTransport : IDisposable
{
    // Human-readable label for logs/UI, e.g. "TCP 192.168.100.15:7374" or
    // "USB: KRONOS".
    string Description { get; }

    // Stable per-Kronos key for the on-disk name / dumped-bank caches (TCP: the
    // host; USB: the device match). Lets a cache survive reconnects to the same
    // instrument regardless of backend.
    string CacheKey { get; }

    // Every MIDI message this transport sends or receives, for the SysEx tool log
    // and inbound-message parsing. RX entries carry RawBytes; TX entries are the
    // hex we sent.
    event Action<SysExTrafficEntry>? Traffic;

    // Complete F0…F7 SysEx messages received on the live stream — the bulk-dump
    // collector gathers multi-message bank dumps and large objects off this.
    event Action<byte[]>? SysExMessageReceived;

    // Pulses on SysEx start and every ~512 B while a large SysEx accumulates, so
    // the dump collector can tell a slowly-arriving large object from a stall.
    event Action? SysExActivity;

    // Open the transport (connect the stream / open the device). Idempotent.
    void Start();

    // Close it and release resources.
    void Stop();

    // Whether the live inbound stream is currently active. Bulk dumps require it.
    bool CanStream { get; }

    // Enable/disable the live inbound stream. TCP: connect/disconnect the 9875
    // monitor (a bandwidth optimisation). USB: no-op — the single device
    // connection inherently carries the stream, and round-trip replies arrive on
    // it too, so it can never be turned off independently.
    void SetStreamEnabled(bool enabled);

    // Probe MIDI/SysEx availability. On success, LastModeData carries the initial
    // mode if the Kronos answered a Mode Request. Returns false when SysEx is
    // unavailable (disabled on the Kronos, no device, or timeout).
    Task<bool> ProbeAsync(int timeoutMs = 8000);
    SysExModeData? LastModeData { get; }

    // Round-trip: send a SysEx request and await the correlated reply (null on
    // timeout/unavailable). expectReplyFunc is the Korg function byte of the
    // expected reply (e.g. 0x42 Mode Data, 0x33 Performance Id) used to correlate
    // on a shared inbound stream; null matches the first Korg reply that arrives.
    Task<byte[]?> QueryAsync(byte[] request, byte? expectReplyFunc = null, int timeoutMs = 3000);

    // Fire-and-forget raw MIDI out. The buffer may contain one or more
    // concatenated messages (SysEx blocks and/or short channel messages).
    Task<bool> SendAsync(byte[] message);

    // Send ONE large SysEx message (a full-object 0x73 write — Combi ~8.9 KB, Set
    // List ~79 KB) reliably, regardless of backend size limits. USB sends it as a
    // single long message; TCP splits it into ctrl-line-sized MIDI_SEND writes the
    // daemon injects contiguously into /proc/.midi_in, so the Kronos reassembles the
    // single F0…F7 from the byte stream. `sysex` MUST be exactly one complete
    // message (F0…F7) — do not pass concatenated messages here.
    Task<bool> SendLargeSysExAsync(byte[] sysex);
}
