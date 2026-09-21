namespace KronosScreenRemote;

// Resolves DeviceFamily from a MIDI Universal Identity Request/Reply, for sessions where
// nothing else can (a USB-only session has no daemon TCP connection at all, so the MODEL
// command MainWindow.Streaming.cs normally reads FAMILY from never runs - see Nautilus
// Research/CLAUDE.md's "SysEx-over-USB access only" note, the live situation this exists for).
// Grill-me decision 2026-09-21: unify device identification through this probe (daemon MODEL
// stays authoritative when a TCP session exists - SysExService.SetDeviceFamily no-ops if it's
// already been told).
//
// Universal Non-realtime Identity, not a Korg-prefixed message (manufacturer byte 0x7E, not
// Korg's 0x42) - request/reply IDs per the MIDI spec, channel-independent:
//   Request: F0 7E 7F 06 01 F7                    (7F = "all call" device ID)
//   Reply:   F0 7E <dev> 06 02 <mfr> <family...> <ver...> F7
// Nautilus's reply is hardware-confirmed: F0 7E 0g 06 02 42 00 01 5D 00 mm 00 vv ww xx 00 F7
// (NAUTILUS_MIDI_MIDI_Implentaion.txt:181) - manufacturer 0x42 (Korg), family 00 01 5D, which
// matches the same 00 01 5D bytes already hardware-confirmed as Nautilus's 6-byte SysEx Exclusive
// Header (sysex_protocol_kronos_vs_nautilus.md). Kronos's own Identity Reply bytes are not
// captured anywhere in this codebase yet, so this probe doesn't try to match them exactly -
// any Korg-manufacturer (0x42) reply that ISN'T the confirmed Nautilus family bytes is treated
// as Kronos, consistent with screenremote.c's own detect_device_model(), which likewise only
// ever distinguishes these two families, never a general "which Korg product is this."
static class MidiDeviceIdentity
{
    static readonly byte[] Request = { 0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7 };

    // Nautilus's confirmed manufacturer+family bytes within the reply (indices 5-8).
    static readonly byte[] NautilusFamily = { 0x42, 0x00, 0x01, 0x5D };

    public static async Task<DeviceFamily> ProbeAsync(IKronosMidiTransport transport, CancellationToken ct, int timeoutMs = 1500)
    {
        var tcs = new TaskCompletionSource<DeviceFamily>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMsg(byte[] m)
        {
            var family = ParseReply(m);
            if (family != DeviceFamily.Unknown) tcs.TrySetResult(family);
        }

        transport.SysExMessageReceived += OnMsg;
        try
        {
            if (!await transport.SendAsync(Request).ConfigureAwait(false))
                return DeviceFamily.Unknown;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(DeviceFamily.Unknown));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            transport.SysExMessageReceived -= OnMsg;
        }
    }

    // F0 7E <dev> 06 02 <mfr> <family bytes...> ... F7
    static DeviceFamily ParseReply(byte[] m)
    {
        if (m.Length < 9 || m[0] != 0xF0 || m[1] != 0x7E || m[3] != 0x06 || m[4] != 0x02)
            return DeviceFamily.Unknown;
        if (m[5] != 0x42) return DeviceFamily.Unknown;   // not Korg
        bool isNautilus = m.Length >= 9 && m[5] == NautilusFamily[0] && m[6] == NautilusFamily[1] &&
                           m[7] == NautilusFamily[2] && m[8] == NautilusFamily[3];
        return isNautilus ? DeviceFamily.Nautilus : DeviceFamily.Kronos;
    }
}
