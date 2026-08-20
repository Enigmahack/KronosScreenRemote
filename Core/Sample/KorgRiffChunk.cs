namespace KronosScreenRemote;

using System.Text;

// Shared chunk framing for the .KMP/.KSF family: [4-byte tag][4-byte big-endian
// length][payload], no outer file header (CKorgRiff). Direct port of
// Tools/sample_editor/kronos_ksc_format.py's module-level helpers - see
// kronosology/docs/interfaces/ksc_kmp_ksf_file_format.md for the format this
// implements and its hardware-confirmation history.
static class KorgRiffChunk
{
    public static List<(string Tag, byte[] Payload)> ReadChunks(byte[] data)
    {
        var chunks = new List<(string, byte[])>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            string tag = Encoding.ASCII.GetString(data, pos, 4);
            uint length = ReadU32BE(data, pos + 4);
            int payloadLen = (int)Math.Min(length, (uint)Math.Max(0, data.Length - pos - 8));
            var payload = data.AsSpan(pos + 8, payloadLen).ToArray();
            chunks.Add((tag, payload));
            pos += 8 + (int)length;
        }
        return chunks;
    }

    public static byte[] BuildChunk(string tag, byte[] payload)
    {
        if (tag.Length != 4) throw new ArgumentException($"chunk tag must be 4 chars: '{tag}'", nameof(tag));
        var result = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes(tag, 0, 4, result, 0);
        WriteU32BE(result, 4, (uint)payload.Length);
        payload.CopyTo(result, 8);
        return result;
    }

    // Space-pad (or truncate) to exactly n bytes - the convention every real Korg name
    // field in this family uses.
    public static byte[] PadBytes(string s, int n)
    {
        var b = Encoding.ASCII.GetBytes(s);
        var result = new byte[n];
        int copyLen = Math.Min(b.Length, n);
        Array.Copy(b, result, copyLen);
        for (int i = copyLen; i < n; i++) result[i] = (byte)' ';
        return result;
    }

    // Split a decoded, possibly stereo-suffixed name into (base, suffix), suffix being
    // "", "-L", or "-R". Every real name field in this family (SMP1/MSP1's short name,
    // NAME's 24-byte field) carries the same logical name+suffix, just independently
    // padded per field width - this is the shared decode step for both.
    public static (string Base, string Suffix) SplitNameSuffix(string text)
    {
        var stripped = text.TrimEnd();
        if (stripped.EndsWith("-L") || stripped.EndsWith("-R"))
            return (stripped[..^2].TrimEnd(), stripped[^2..]);
        return (stripped, "");
    }

    // Encode base+suffix into a `width`-byte space-padded field with the suffix
    // RIGHT-ALIGNED at the very end - confirmed independently for both the 16/18-byte
    // short name and the 24-byte NAME chunk, each at its own width.
    public static byte[] EncodeNameField(string baseName, string suffix, int width)
    {
        baseName = baseName[..Math.Min(baseName.Length, Math.Max(0, width - suffix.Length))];
        int pad = width - baseName.Length - suffix.Length;
        string text = baseName + new string(' ', Math.Max(0, pad)) + suffix;
        var bytes = Encoding.ASCII.GetBytes(text);
        var result = new byte[width];
        int copyLen = Math.Min(bytes.Length, width);
        Array.Copy(bytes, result, copyLen);
        for (int i = copyLen; i < width; i++) result[i] = (byte)' ';
        return result;
    }

    public static uint ReadU32BE(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    public static void WriteU32BE(byte[] data, int offset, uint value)
    {
        data[offset]     = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }
}
