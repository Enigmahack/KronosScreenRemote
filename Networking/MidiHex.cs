namespace KronosScreenRemote;

using System.Text;

// Shared MIDI byte/hex helpers used by the transport layer. Keeps the hex
// encode/decode and outbound-message splitting in one place so the TCP and USB
// backends agree byte-for-byte.
static class MidiHex
{
    // "F0 42 30 68 12 F7" (space-separated, upper-case) - the wire format the
    // daemon's SYSEX / MIDI_SEND commands expect and the SysEx tool log shows.
    //
    // maxBytes caps how many bytes are rendered, appending a " ... (+N bytes)" tail when the
    // input is longer - a bulk SysEx object (a Set List is ~79 KB) would otherwise build a
    // ~200k-char string, expensive to allocate and to lay out in a wrapping TextBlock. The
    // default renders everything, so wire-format callers (which must emit no ellipsis) are
    // unaffected.
    public static string ToHex(byte[] bytes, int maxBytes = int.MaxValue)
    {
        int n = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder(n * 3 + 20);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        if (n < bytes.Length) sb.Append($" ... (+{bytes.Length - n} bytes)");
        return sb.ToString();
    }

    // Parse a hex string ("F0 42 ..." or "F04230...") to bytes; null on malformed input.
    public static byte[]? ToBytes(string hex)
    {
        var clean = hex.Replace(" ", "");
        if (clean.Length == 0 || clean.Length % 2 != 0) return null;
        try
        {
            var bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return bytes;
        }
        catch { return null; }
    }

    // Data-byte count for a MIDI status byte (channel + system common). Matches
    // MidiStreamParser's decoder; used to carve a concatenated outbound stream into
    // discrete messages so the USB backend can send each one correctly.
    public static int DataBytesFor(int status) => (status & 0xF0) switch
    {
        0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0 => 2,
        0xC0 or 0xD0 => 1,
        _ => status switch
        {
            0xF1 or 0xF3 => 1,
            0xF2         => 2,
            _            => 0,
        }
    };

    // Split a buffer that may hold several concatenated MIDI messages (the dump
    // collector batches many SysEx requests into one send) into individual
    // messages. SysEx runs F0...F7; channel/common messages are status + N data
    // bytes; single-byte system real-time/reset pass through. Running status is
    // NOT assumed - every message we emit carries an explicit status byte.
    //
    // Malformed fragments are DROPPED, never forwarded: an unterminated SysEx tail
    // (no F7), a channel/common message truncated by the buffer end, an orphan F7
    // (End-of-Exclusive with no F0), and stray data bytes. Forwarding any of these
    // as a status-only or headless message would transmit spurious events (e.g.
    // `90` alone becomes Note-On 0/0 on the wire).
    public static List<byte[]> SplitMessages(byte[] stream)
    {
        var msgs = new List<byte[]>();
        int i = 0, n = stream.Length;
        while (i < n)
        {
            byte b = stream[i];

            if (b == 0xF0)                          // SysEx - must terminate with F7
            {
                int end = Array.IndexOf(stream, (byte)0xF7, i + 1);
                if (end < 0) break;                 // unterminated tail - drop
                msgs.Add(stream[i..(end + 1)]);
                i = end + 1;
            }
            else if (b >= 0xF8)                     // system real-time / reset - single byte
            {
                msgs.Add([b]);
                i++;
            }
            else if (b == 0xF7)                     // orphan End-of-Exclusive - skip
            {
                i++;
            }
            else if ((b & 0x80) != 0)               // channel / system-common status
            {
                int need = DataBytesFor(b);
                if (i + 1 + need > n) break;         // truncated tail - drop
                msgs.Add(stream[i..(i + 1 + need)]);
                i += 1 + need;
            }
            else
            {
                i++;                                // orphan data byte with no status - skip
            }
        }
        return msgs;
    }
}
