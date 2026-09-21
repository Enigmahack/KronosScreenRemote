using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;

namespace KronosScreenRemote;

// Speaks stream protocol v3 only (docs/api.md §3.0/§4.5, daemon 3.0.2+). v3 is a strict
// superset of v2 on Kronos (same INDEX8 pixels + palette, just delivered as rects) and the
// only protocol that works on the Nautilus (RGB565LE, no palette) - the daemon docs
// themselves recommend v3-only for new clients. A pre-3.0.2 daemon answers a v3 hello with
// plain status 0x01 (indistinguishable on the wire from an auth failure); this client does
// not attempt a v2 fallback, so such a daemon must be upgraded.
sealed class StreamReceiver : IStreamReceiver
{
    public const int  StreamPort = 7373;
    const  byte  ModePull   = 0x01;
    const  byte  ModeChange = 0x02;
    const  byte  HelloVersion = 0x03;
    static readonly byte[] Magic = "KSCR"u8.ToArray();

    // Sanity bound on a v3 frame's wire length (4-byte len field's value), so a corrupt/desynced
    // stream can't make the client allocate an unbounded buffer. Generous enough for a raw
    // uncompressed full Nautilus frame (9 + 800*480*2 = 768009) plus PackBits' worst-case
    // ~1/128 expansion, with headroom.
    const int MaxRectPayload = 9 + 800 * 600 * 2 + 16 * 1024;

    readonly string _host;
    readonly int    _port;
    readonly byte   _mode;
    readonly byte   _fps;
    readonly string _user;
    readonly string _pass;

    Socket?  _sock;
    Thread?  _thread;
    volatile bool _stop;
    byte[]?   _canvas;   // persistent reconstructed frame, native pixel format; rects accumulate here
    int       _bytesPerPixel;

    // Latest fully-decoded frame, published under _frameLock.  The render thread copies it out via
    // TryCopyLatestFrame under the same lock, so the receive thread and the UI thread never touch
    // the same buffer at once (the old 3-slot ring had no such guard and could tear a frame under
    // a UI stall).
    readonly object _frameLock = new();
    byte[]? _latest;
    bool    _hasFrame;

    public int  Width   { get; private set; }
    public int  Height  { get; private set; }
    public StreamPixelFormat Fmt { get; private set; }
    public int  Bpp     { get; private set; }
    public PaletteEntry[] Palette { get; private set; } = Array.Empty<PaletteEntry>();

    public event Action? Disconnected;

    public StreamReceiver(string host, int port, bool pullMode, int fps, string user, string pass)
    {
        _host = host;
        _port = port;
        _mode = pullMode ? ModePull : ModeChange;
        // Protocol: 1-15, 0 = daemon uses its maximum. Clamp negative garbage from a
        // hand-edited settings file / CLI arg to 0 (daemon max) rather than letting a
        // negative int wrap to 200+ through the byte cast (daemon clamps that to 15).
        _fps  = (byte)Math.Clamp(fps, 0, 15);
        _user = user;
        _pass = pass;
    }

    // Async connect with a hard 10-second watchdog implemented via Task.WhenAny + socket close.
    //
    // CancellationToken alone is not reliable for socket operations on Windows: WFP callouts
    // (Defender, antivirus, VPN drivers) can intercept I/O and keep it pending even after the
    // token fires.  Calling _sock.Close() is the only guaranteed way to unblock a pending op -
    // it causes the OS to abort the I/O and complete the task with a SocketException immediately.
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _sock.NoDelay = true;
        // Advertise a large receive window from the first SYN so the server can
        // push a full 480 KB frame without stalling for ACKs.  Must be set before
        // ConnectAsync - the value is included in the TCP SYN/SYN-ACK handshake.
        _sock.ReceiveBufferSize = 512 * 1024;
        // TCP keepalive: detect Kronos power-off (hard reset, no FIN) within ~25 s.
        // Without this the OS won't probe a silent dead connection for up to 2 hours,
        // so change-mode's Poll(5000)/continue loop never terminates.
        // When keepalive marks the socket dead the next Poll() returns true (error = readable),
        // RecvAllInto gets 0 bytes, the loop breaks, and Disconnected fires normally.
        _sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            // SIO_KEEPALIVE_VALS: idle=5 s, interval=2 s; Windows default probe count = 10 → 25 s max.
            var ka = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(ka, 0);      // enable
            BitConverter.GetBytes(5_000u).CopyTo(ka, 4);  // idle before first probe (ms)
            BitConverter.GetBytes(2_000u).CopyTo(ka, 8);  // interval between probes (ms)
            _sock.IOControl(IOControlCode.KeepAliveValues, ka, null);
        }
        catch { /* SIO_KEEPALIVE_VALS unavailable - SO_KEEPALIVE still active (OS default timing) */ }

        var handshake = DoHandshakeAsync(ct);
        var watchdog  = Task.Delay(10_000);          // plain delay - no CancellationToken

        if (await Task.WhenAny(handshake, watchdog) != handshake)
        {
            // Watchdog fired before handshake finished.
            // Close the socket to signal abort to any pending OS-level I/O.
            try { _sock.Close(); } catch { }

            // Do NOT await handshake - Windows WFP callouts (Defender, AV, VPN drivers) can
            // keep socket I/O pending even after Close(), so await would block indefinitely.
            // ContinueWith observes the eventual exception to prevent UnobservedTaskException.
            _ = handshake.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.None);

            throw new TimeoutException(
                $"Connection to {_host}:{_port} timed out after 10 s.\n\n" +
                "Possible causes: Firewall blocking port 7373, \n" +
                "screenremote daemon not running, cable unplugged. \n");
        }

        await handshake;   // re-await to propagate any non-timeout exception (e.g. connection refused)
    }

    async Task DoHandshakeAsync(CancellationToken ct)
    {
        Console.WriteLine($"[stream] connecting to {_host}:{_port}...");
        await _sock!.ConnectAsync(_host, _port, ct);
        Console.WriteLine("[stream] TCP connected - sending v3 handshake...");

        var uBytes = Encoding.ASCII.GetBytes(_user);
        var pBytes = Encoding.ASCII.GetBytes(_pass);
        if (uBytes.Length > 64 || pBytes.Length > 128)
            throw new ArgumentException("FTP username or password exceeds allowed length.");
        byte[] hello = [.. Magic, HelloVersion, _mode, _fps, (byte)uBytes.Length, (byte)pBytes.Length,
                        .. uBytes, .. pBytes];
        await _sock.SendAllAsync(hello, ct);
        Console.WriteLine("[stream] awaiting server handshake response...");

        // Read 5-byte status header first; full payload only follows on success.
        var hdrRsp = await RecvAllAsync(_sock, 5, ct);
        if (hdrRsp == null || !hdrRsp.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid response from daemon");

        byte status = hdrRsp[4];
        if (status == 0x01)
            throw new UnauthorizedAccessException(
                "FTP authentication rejected by Kronos daemon (or the daemon is older than 3.0.2 and " +
                "does not understand the v3 hello - status 0x01 is ambiguous between the two).");
        if (status == 0x02)
            throw new IOException("Kronos could not look up credentials - user not found or account locked.");
        if (status == 0x03 || status == 0x04)
        {
            var verBytes = await RecvAllAsync(_sock, 2, ct);
            string verInfo = verBytes != null
                ? $" (daemon supports hello versions 0x{verBytes[0]:X2}-0x{verBytes[1]:X2})"
                : "";
            string what = status == 0x03
                ? "the daemon accepted v3 but reports this hardware's pixel format needs a newer version than it implements"
                : "the daemon does not support hello version 0x03 (VERSION_MISMATCH)";
            throw new InvalidDataException($"Handshake rejected by daemon: {what}{verInfo}");
        }
        if (status != 0x00)
            throw new InvalidDataException($"Handshake rejected by daemon (status 0x{status:X2})");

        // w(2) h(2) fmt(1) bpp(1) enc(1) flags(1)
        var head = await RecvAllAsync(_sock, 8, ct);
        if (head == null)
            throw new InvalidDataException("Handshake payload truncated");

        Width  = head[0] | (head[1] << 8);
        Height = head[2] | (head[3] << 8);
        Fmt    = (StreamPixelFormat)head[4];
        Bpp    = head[5];
        // head[6] (enc) and head[7] (flags) are informational only - every frame carries its own enc byte.

        if (Bpp != 8 && Bpp != 16)
            throw new InvalidDataException($"Handshake reported unsupported bpp {Bpp}");
        _bytesPerPixel = Bpp / 8;

        if (Fmt == StreamPixelFormat.Index8)
        {
            var pal = await RecvAllAsync(_sock, 256 * 3, ct);
            if (pal == null)
                throw new InvalidDataException("Handshake palette truncated");
            var entries = new PaletteEntry[256];
            for (int i = 0; i < 256; i++)
                entries[i] = new PaletteEntry(pal[i * 3], pal[i * 3 + 1], pal[i * 3 + 2]);
            Palette = entries;
        }
        else
        {
            Palette = Array.Empty<PaletteEntry>();
        }

        Console.WriteLine($"[stream] handshake OK - {Width}×{Height} {Fmt} {Bpp}bpp");

        // Pre-allocate the persistent canvas + the published snapshot buffer.  _canvas
        // accumulates all rect updates; _latest is a stable snapshot the render thread copies out
        // under _frameLock, so accumulation and rendering never share the same buffer.
        int frameSize = Width * Height * _bytesPerPixel;
        _canvas = new byte[frameSize];
        _latest = new byte[frameSize];

        _stop   = false;
        _thread = new Thread(RecvLoop) { IsBackground = true, Name = "StreamReceiver" };
        _thread.Start();
    }

    void RecvLoop()
    {
        double interval = _mode == ModePull && _fps > 0 ? 1000.0 / _fps : 0;
        var hdrBuf = new byte[4];
        var payloadBuf = new byte[4096];   // grown on demand, capped by MaxRectPayload
        bool firstPullRequest = true;

        // Snapshot the socket into a loop-local variable so Dispose() can safely null
        // _sock without the loop observing the null mid-flight. All socket ops in this
        // method go through `sock`, never `_sock!`.
        Socket? sock = _sock;
        try
        {
            while (!_stop && sock != null)
            {
                if (_mode == ModePull)
                {
                    // v3 pull: 0xFF requests a full frame, 0xFE a delta since the last frame this
                    // client received. Only the very first request needs the full frame; every
                    // later poll asks for a delta, which is both smaller and what keeps the
                    // Nautilus's byte-rate cap from being spent on unchanged pixels.
                    sock.Send([firstPullRequest ? (byte)0xFF : (byte)0xFE]);
                    firstPullRequest = false;
                    if (!Poll(sock, 5000)) break;
                }
                else
                {
                    // NOTE: no initial-frame pull here. The daemon pushes a full frame
                    // immediately on connect in change mode (screenremote.c's
                    // client_just_connected path), and it never reads from the client
                    // socket in this mode, so a request byte here would sit unread in the
                    // daemon's recv buffer forever. A forced resync after drift is what the
                    // REFRESH ctrl command is for.
                    if (!Poll(sock, 5000)) continue; // idle gap is normal - Kronos screen unchanged
                    // Dead-connection detection is handled entirely by TCP keepalive (set in
                    // ConnectAsync).  When keepalive fails, the socket enters error state,
                    // Poll returns true (error = readable), and RecvAllInto breaks the loop.
                }

                if (!RecvAllInto(sock, hdrBuf, 4)) break;
                int len = hdrBuf[0] | (hdrBuf[1] << 8) | (hdrBuf[2] << 16) | (hdrBuf[3] << 24);

                if (len < 9 || len > MaxRectPayload)
                {
                    Console.Error.WriteLine(
                        $"[stream] invalid v3 frame length {len} - dropping connection");
                    break;
                }
                if (payloadBuf.Length < len) payloadBuf = new byte[len];
                if (!RecvAllInto(sock, payloadBuf, 0, len)) break;

                var result = V3FrameDecoder.ApplyRect(payloadBuf, len, _canvas!, Width, Height, _bytesPerPixel);
                if (result == V3RectResult.Malformed)
                {
                    Console.Error.WriteLine("[stream] malformed v3 rect - dropping connection");
                    break;
                }
                // Unchanged (w==0||h==0, the explicit "nothing changed" 0xFE reply) must NOT
                // publish - the canvas didn't move, so there is nothing new for the render
                // thread to pick up. Publishing anyway would make every idle pull-mode poll
                // (up to 15/s) pay for a full canvas memcpy + LUT blit + dirty-rect/InvalidateVisual
                // on a static screen, defeating the whole point of requesting a delta.
                if (result == V3RectResult.Applied)
                    PublishFrame(_canvas!.Length);

                if (_mode == ModePull && interval > 0)
                    Thread.Sleep((int)interval);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[stream] receive error: {ex.Message}"); }
        finally
        {
            if (!_stop) Disconnected?.Invoke();
        }
    }

    // Copy the freshly reconstructed canvas into the published snapshot.  Held for exactly
    // one memcpy so the receive loop is never blocked on the render thread.
    void PublishFrame(int frameSize)
    {
        lock (_frameLock)
        {
            Buffer.BlockCopy(_canvas!, 0, _latest!, 0, frameSize);
            _hasFrame = true;
        }
    }

    // Render-thread entry point: copy the latest frame into dst if one has arrived since the last
    // call, returning false (dst untouched) when there is nothing new or dst is too small.  The
    // lock serializes this against PublishFrame so a frame can never be read half-written.
    public bool TryCopyLatestFrame(byte[] dst)
    {
        lock (_frameLock)
        {
            if (!_hasFrame || _latest == null || dst.Length < _latest.Length) return false;
            Buffer.BlockCopy(_latest, 0, dst, 0, _latest.Length);
            _hasFrame = false;
            return true;
        }
    }

    bool Poll(Socket sock, int timeoutMs)
    {
        try { return sock.Poll(timeoutMs * 1000, SelectMode.SelectRead); }
        catch { return false; }
    }

    static bool RecvAllInto(Socket sock, byte[] buf, int n) =>
        RecvAllInto(sock, buf, 0, n);

    static bool RecvAllInto(Socket sock, byte[] buf, int offset, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = sock.Receive(buf, offset + got, n - got, SocketFlags.None);
            if (r == 0) return false;
            got += r;
        }
        return true;
    }

    static async Task<byte[]?> RecvAllAsync(Socket sock, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await sock.ReceiveAsync(new Memory<byte>(buf, got, n - got), SocketFlags.None, ct);
            if (r == 0) return null;
            got += r;
        }
        return buf;
    }

    public void Dispose()
    {
        _stop = true;
        try { _sock?.Shutdown(SocketShutdown.Both); } catch { }
        try { _sock?.Close(); } catch { }

        // Join the receive thread with a bounded wait so the loop is deterministicly
        // done before this returns.  Guard: if Disconnected's handler calls Dispose
        // (the handler runs on _thread), joining would deadlock.
        var t = _thread;
        if (t != null && t != Thread.CurrentThread && t.IsAlive)
            t.Join(TimeSpan.FromSeconds(3));

        _sock = null;
    }
}

// Applied: the rect was decoded and copied into the canvas. Unchanged: a valid "nothing
// changed" reply (w==0||h==0) - the canvas was NOT touched. Malformed: the payload is
// corrupt/desynced/out-of-bounds - the caller must treat the connection as unrecoverable.
// Kept as a 3-way result (not bool) specifically so the caller can skip publishing a frame
// on Unchanged instead of re-publishing an identical canvas on every idle pull-mode poll.
internal enum V3RectResult { Applied, Unchanged, Malformed }

// Pure, socket-free decoding of one v3 frame payload (everything after the 4-byte length
// prefix - docs/api.md §4.5). Factored out of StreamReceiver so it can be exercised by
// StreamReceiverV3SelfTests without a live daemon.
static class V3FrameDecoder
{
    // Applies payload[0..payloadLen) to `canvas` (Width*Height*bytesPerPixel bytes, row-major,
    // no padding).
    public static V3RectResult ApplyRect(byte[] payload, int payloadLen, byte[] canvas,
                                          int canvasWidth, int canvasHeight, int bytesPerPixel)
    {
        if (payloadLen < 9 || payload.Length < payloadLen) return V3RectResult.Malformed;

        int enc = payload[0];
        int x0  = payload[1] | (payload[2] << 8);
        int y0  = payload[3] | (payload[4] << 8);
        int w   = payload[5] | (payload[6] << 8);
        int h   = payload[7] | (payload[8] << 8);

        if (w == 0 || h == 0) return V3RectResult.Unchanged;
        if (x0 < 0 || y0 < 0 || w < 0 || h < 0 || x0 + w > canvasWidth || y0 + h > canvasHeight)
            return V3RectResult.Malformed;

        int dataOffset = 9;
        int dataLen    = payloadLen - 9;
        int decodedLen = w * h * bytesPerPixel;
        if (decodedLen <= 0) return V3RectResult.Malformed;

        byte[]? rented = null;
        try
        {
            byte[] decoded;
            int decodedOffset;
            switch (enc)
            {
                case 0: // raw
                    if (dataLen != decodedLen) return V3RectResult.Malformed;
                    decoded = payload;
                    decodedOffset = dataOffset;
                    break;
                case 1: // PackBits
                    rented = ArrayPool<byte>.Shared.Rent(decodedLen);
                    if (PackbitsExpand(payload, dataOffset, dataLen, rented, decodedLen) != decodedLen)
                        return V3RectResult.Malformed;
                    decoded = rented;
                    decodedOffset = 0;
                    break;
                case 2: // zlib / RFC 1950
                    rented = ArrayPool<byte>.Shared.Rent(decodedLen);
                    if (!ZlibInflateExact(payload, dataOffset, dataLen, rented, decodedLen))
                        return V3RectResult.Malformed;
                    decoded = rented;
                    decodedOffset = 0;
                    break;
                default:
                    return V3RectResult.Malformed;
            }

            int srcPitch = w * bytesPerPixel;
            int dstPitch = canvasWidth * bytesPerPixel;
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(decoded, decodedOffset + row * srcPitch,
                                  canvas, (y0 + row) * dstPitch + x0 * bytesPerPixel, srcPitch);
            return V3RectResult.Applied;
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // PackBits decoder (Apple/TIFF variant). Returns number of bytes written to dst.
    static int PackbitsExpand(byte[] src, int srcOffset, int srcLen, byte[] dst, int dstLen)
    {
        int si = srcOffset, sEnd = srcOffset + srcLen, di = 0;
        while (si < sEnd && di < dstLen)
        {
            int n = (sbyte)src[si++];
            if (n >= 0)
            {
                int count = n + 1;
                if (si + count > sEnd || di + count > dstLen) break;
                Buffer.BlockCopy(src, si, dst, di, count);
                si += count; di += count;
            }
            else if (n != -128)
            {
                int count = 1 - n;
                if (si >= sEnd || di + count > dstLen) break;
                byte b = src[si++];
                for (int k = 0; k < count; k++) dst[di++] = b;
            }
            // n == -128 (0x80): NOP - skip
        }
        return di;
    }

    // Inflates exactly dst.Length bytes of a zlib (RFC 1950) stream. Returns false if the
    // stream errors, ends early, or decodes to a different length than expected.
    static bool ZlibInflateExact(byte[] src, int srcOffset, int srcLen, byte[] dst, int dstLen)
    {
        try
        {
            using var ms = new MemoryStream(src, srcOffset, srcLen, writable: false);
            using var z  = new ZLibStream(ms, CompressionMode.Decompress);
            int got = 0;
            while (got < dstLen)
            {
                int r = z.Read(dst, got, dstLen - got);
                if (r == 0) break;
                got += r;
            }
            return got == dstLen;
        }
        catch { return false; }
    }
}
