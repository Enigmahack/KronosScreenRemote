using System.IO;
using System.IO.Compression;

namespace KronosScreenRemote;

// Off-hardware coverage for V3FrameDecoder.ApplyRect (docs/api.md §4.5) - the part of the
// Nautilus wiring that's actually testable without a live daemon. No Nautilus unit was
// available to verify this against real hardware; these lock in the wire-format contract
// documented in api.md §4.5/§4.5.1 (worked examples) instead. Wired into --librarian-selftest.
static class StreamReceiverV3SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── raw (enc=0), 1 byte/pixel (INDEX8), full-canvas rect ──
        {
            var canvas = new byte[2 * 2];
            var payload = BuildPayload(enc: 0, x0: 0, y0: 0, w: 2, h: 2,
                                        data: [1, 2, 3, 4]);
            var result = V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 2, 2, 1);
            Check("raw-index8-applies",
                result == V3RectResult.Applied && canvas.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        }

        // ── raw (enc=0), 2 bytes/pixel (RGB565LE), off-origin partial rect into a larger canvas ──
        {
            var canvas = new byte[4 * 3 * 2];   // 4x3 canvas, 2 bytes/pixel
            // 1x2 rect at (1,1): two RGB565LE words 0x1234, 0x5678 (LE bytes 34 12 / 78 56)
            var payload = BuildPayload(enc: 0, x0: 1, y0: 1, w: 1, h: 2,
                                        data: [0x34, 0x12, 0x78, 0x56]);
            var result = V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 4, 3, 2);
            var expect = new byte[4 * 3 * 2];
            expect[(1 * 4 + 1) * 2 + 0] = 0x34; expect[(1 * 4 + 1) * 2 + 1] = 0x12;
            expect[(2 * 4 + 1) * 2 + 0] = 0x78; expect[(2 * 4 + 1) * 2 + 1] = 0x56;
            Check("raw-rgb565-off-origin",
                result == V3RectResult.Applied && canvas.SequenceEqual(expect));
        }

        // ── PackBits (enc=1): literal run then repeat run, matching docs/api.md §4.3 ──
        {
            var canvas = new byte[4];
            // header 0x01 = literal run of 2 bytes (0xAA,0xBB), header 0xFF = repeat next byte
            // (257-255=2) times (0xCC,0xCC) -> decodes to AA BB CC CC (4 bytes = a 4x1 INDEX8 rect)
            var packed = new byte[] { 0x01, 0xAA, 0xBB, 0xFF, 0xCC };
            var payload = BuildPayload(enc: 1, x0: 0, y0: 0, w: 4, h: 1, data: packed);
            var result = V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 4, 1, 1);
            Check("packbits-decodes",
                result == V3RectResult.Applied && canvas.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xCC }));
        }

        // ── zlib/deflate (enc=2), RFC 1950 - round-trip through the real compressor ──
        {
            var canvas = new byte[4];
            var plain = new byte[] { 10, 20, 30, 40 };
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(plain, 0, plain.Length);
            var compressed = ms.ToArray();
            var payload = BuildPayload(enc: 2, x0: 0, y0: 0, w: 4, h: 1, data: compressed);
            var result = V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 4, 1, 1);
            Check("zlib-decodes", result == V3RectResult.Applied && canvas.SequenceEqual(plain));
        }

        // ── empty rect (w=0 or h=0): explicit "nothing changed" - valid no-op, canvas untouched,
        //    and reported as Unchanged (not Applied) so StreamReceiver.RecvLoop knows NOT to
        //    publish a frame for it - every idle pull-mode 0xFE poll sends one of these. ──
        {
            var canvas = new byte[] { 9, 9, 9, 9 };
            var payload = BuildPayload(enc: 0, x0: 0, y0: 0, w: 0, h: 0, data: []);
            var result = V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 2, 2, 1);
            Check("empty-rect-is-unchanged-not-applied",
                result == V3RectResult.Unchanged && canvas.SequenceEqual(new byte[] { 9, 9, 9, 9 }));
        }

        // ── rect exceeding canvas bounds: rejected, not clamped (matches the client's
        //    "desync -> drop the connection" policy elsewhere in StreamReceiver) ──
        {
            var canvas = new byte[4];
            var payload = BuildPayload(enc: 0, x0: 1, y0: 0, w: 4, h: 1, data: new byte[4]);
            Check("oob-rect-rejected",
                V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 4, 1, 1) == V3RectResult.Malformed);
        }

        // ── raw payload whose data length doesn't match w*h*bpp: rejected ──
        {
            var canvas = new byte[4];
            var payload = BuildPayload(enc: 0, x0: 0, y0: 0, w: 4, h: 1, data: new byte[3]);
            Check("short-raw-payload-rejected",
                V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 4, 1, 1) == V3RectResult.Malformed);
        }

        // ── payload shorter than the fixed 9-byte rect header: rejected ──
        {
            var canvas = new byte[4];
            var payload = new byte[] { 0, 0, 0 };
            Check("truncated-header-rejected",
                V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 4, 1, 1) == V3RectResult.Malformed);
        }

        // ── unknown enc byte: rejected ──
        {
            var canvas = new byte[4];
            var payload = BuildPayload(enc: 9, x0: 0, y0: 0, w: 4, h: 1, data: new byte[4]);
            Check("unknown-enc-rejected",
                V3FrameDecoder.ApplyRect(payload, payload.Length, canvas, 4, 1, 1) == V3RectResult.Malformed);
        }

        return fails;
    }

    // Mirrors docs/api.md §4.5's frame layout (everything after the 4-byte outer length prefix,
    // which StreamReceiver.RecvLoop strips before calling ApplyRect): enc, x0,y0,w,h (LE16 each), data.
    static byte[] BuildPayload(int enc, int x0, int y0, int w, int h, byte[] data)
    {
        var p = new byte[9 + data.Length];
        p[0] = (byte)enc;
        p[1] = (byte)x0; p[2] = (byte)(x0 >> 8);
        p[3] = (byte)y0; p[4] = (byte)(y0 >> 8);
        p[5] = (byte)w;  p[6] = (byte)(w >> 8);
        p[7] = (byte)h;  p[8] = (byte)(h >> 8);
        data.CopyTo(p, 9);
        return p;
    }
}
