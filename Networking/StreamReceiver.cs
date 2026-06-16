using System.IO;
using System.Net.Sockets;
using System.Text;

namespace KronosScreenRemote;

sealed class StreamReceiver : IStreamReceiver
{
    public const int  StreamPort = 7373;
    const  byte  ModePull   = 0x01;
    const  byte  ModeChange = 0x02;
    static readonly byte[] Magic = "KSCR"u8.ToArray();

    readonly string _host;
    readonly int    _port;
    readonly byte   _mode;
    readonly byte   _fps;
    readonly string _user;
    readonly string _pass;

    Socket?  _sock;
    Thread?  _thread;
    volatile bool _stop;
    byte[][]? _frameBufs;   // ring buffer slots passed to MainWindow — stable snapshots
    int       _frameBufIdx;
    byte[]?   _masterFrame; // persistent reconstructed frame; dirty rects accumulate here
    byte[]?   _rleBuf;      // scratch for receiving RLE-encoded dirty rect payload

    public int  Width   { get; private set; }
    public int  Height  { get; private set; }
    public PaletteEntry[] Palette { get; private set; } = Array.Empty<PaletteEntry>();

    public event Action<byte[]>? FrameReceived;
    public event Action?         Disconnected;

    public StreamReceiver(string host, int port, bool pullMode, int fps, string user, string pass)
    {
        _host = host;
        _port = port;
        _mode = pullMode ? ModePull : ModeChange;
        _fps  = (byte)Math.Min(fps, 15);
        _user = user;
        _pass = pass;
    }

    // Async connect with a hard 10-second watchdog implemented via Task.WhenAny + socket close.
    //
    // CancellationToken alone is not reliable for socket operations on Windows: WFP callouts
    // (Defender, antivirus, VPN drivers) can intercept I/O and keep it pending even after the
    // token fires.  Calling _sock.Close() is the only guaranteed way to unblock a pending op —
    // it causes the OS to abort the I/O and complete the task with a SocketException immediately.
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _sock.NoDelay = true;
        // Advertise a large receive window from the first SYN so the server can
        // push a full 480 KB frame without stalling for ACKs.  Must be set before
        // ConnectAsync — the value is included in the TCP SYN/SYN-ACK handshake.
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
        catch { /* SIO_KEEPALIVE_VALS unavailable — SO_KEEPALIVE still active (OS default timing) */ }

        var handshake = DoHandshakeAsync(ct);
        var watchdog  = Task.Delay(10_000);          // plain delay — no CancellationToken

        if (await Task.WhenAny(handshake, watchdog) != handshake)
        {
            // Watchdog fired before handshake finished.
            // Close the socket to signal abort to any pending OS-level I/O.
            try { _sock.Close(); } catch { }

            // Do NOT await handshake — Windows WFP callouts (Defender, AV, VPN drivers) can
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
        Console.WriteLine($"[stream] connecting to {_host}:{_port}…");
        await _sock!.ConnectAsync(_host, _port, ct);
        Console.WriteLine("[stream] TCP connected — sending handshake…");

        var uBytes = Encoding.ASCII.GetBytes(_user);
        var pBytes = Encoding.ASCII.GetBytes(_pass);
        if (uBytes.Length > 64 || pBytes.Length > 128)
            throw new ArgumentException("FTP username or password exceeds allowed length.");
        byte[] hello = [.. Magic, 0x02, _mode, _fps, (byte)uBytes.Length, (byte)pBytes.Length,
                        .. uBytes, .. pBytes];
        await _sock.SendAsync(hello.AsMemory(), ct);
        Console.WriteLine("[stream] awaiting server handshake response…");

        // Read 5-byte status header first; full payload only follows on success.
        var hdrRsp = await RecvAllAsync(_sock, 5, ct);
        if (hdrRsp == null || !hdrRsp.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid response from daemon");

        byte status = hdrRsp[4];
        if (status == 0x01)
            throw new UnauthorizedAccessException("FTP authentication rejected by Kronos daemon.");
        if (status == 0x02)
            throw new IOException("Kronos could not look up credentials — user not found or account locked.");
        if (status != 0x00)
            throw new InvalidDataException($"Handshake rejected by daemon (status 0x{status:X2})");

        var payload = await RecvAllAsync(_sock, 2 + 2 + 256 * 3, ct);
        if (payload == null)
            throw new InvalidDataException("Handshake payload truncated");

        Width  = payload[0] | (payload[1] << 8);
        Height = payload[2] | (payload[3] << 8);

        var pal = new PaletteEntry[256];
        for (int i = 0; i < 256; i++)
            pal[i] = new PaletteEntry(payload[4 + i * 3], payload[4 + i * 3 + 1], payload[4 + i * 3 + 2]);
        Palette = pal;

        Console.WriteLine($"[stream] handshake OK — {Width}×{Height}");

        // Pre-allocate 3 ring slots (passed to MainWindow) + a persistent master frame.
        // _masterFrame accumulates all updates; ring slots carry stable snapshots to the
        // render thread so dirty-rect accumulation and rendering never share the same buffer.
        int frameSize = Width * Height;
        _masterFrame = new byte[frameSize];
        _rleBuf      = new byte[frameSize];   // worst-case RLE size is < frame_bytes (daemon guards)
        _frameBufs   = new[] { new byte[frameSize], new byte[frameSize], new byte[frameSize] };
        _frameBufIdx = 0;

        _stop   = false;
        _thread = new Thread(RecvLoop) { IsBackground = true, Name = "StreamReceiver" };
        _thread.Start();
    }

    void RecvLoop()
    {
        double interval = _mode == ModePull && _fps > 0 ? 1000.0 / _fps : 0;
        var hdrBuf = new byte[4];
        bool firstFrame = true;
        try
        {
            while (!_stop)
            {
                if (_mode == ModePull)
                {
                    _sock!.Send([(byte)0xFF]);
                    if (!Poll(5000)) break;
                }
                else
                {
                    // Pull the first frame so the client always starts with the current
                    // screen state.  Change-driven mode only sends when the screen changes,
                    // so a static Kronos display means the client sees nothing until the
                    // UI moves.
                    if (firstFrame)
                        _sock!.Send([(byte)0xFF]);
                    firstFrame = false;
                    if (!Poll(5000)) continue; // idle gap is normal — Kronos screen unchanged
                    // Dead-connection detection is handled entirely by TCP keepalive (set in
                    // ConnectAsync).  When keepalive fails, the socket enters error state,
                    // Poll returns true (error = readable), and RecvAllInto breaks the loop.
                }

                if (!RecvAllInto(_sock!, hdrBuf, 4)) break;
                int len = hdrBuf[0] | (hdrBuf[1] << 8) | (hdrBuf[2] << 16) | (hdrBuf[3] << 24);

                byte[] data;
                int frameSize = _masterFrame!.Length;
                if (len == frameSize)
                {
                    if (!RecvAllInto(_sock!, _masterFrame, 0, frameSize)) break;
                    data = _frameBufs![_frameBufIdx++ % _frameBufs.Length];
                    Buffer.BlockCopy(_masterFrame, 0, data, 0, frameSize);
                }
                else if (len > 4 && len < frameSize)
                {
                    // Dirty rect with PackBits RLE — decode into _masterFrame, snapshot to ring slot.
                    var subHdr = new byte[4];
                    if (!RecvAllInto(_sock!, subHdr, 0, 4)) break;
                    int firstRow = subHdr[0] | (subHdr[1] << 8);
                    int rowCount = subHdr[2] | (subHdr[3] << 8);
                    int rleBytes = len - 4;
                    int rawBytes = rowCount * Width;
                    if (rawBytes > frameSize || firstRow + rowCount > Height ||
                        rleBytes > _rleBuf!.Length) break;
                    if (!RecvAllInto(_sock!, _rleBuf, 0, rleBytes)) break;
                    if (PackbitsExpand(_rleBuf, rleBytes, _masterFrame!, firstRow * Width, rawBytes) != rawBytes) break;
                    data = _frameBufs![_frameBufIdx++ % _frameBufs.Length];
                    Buffer.BlockCopy(_masterFrame, 0, data, 0, frameSize);
                }
                else
                {
                    // Unknown packet size — drain and skip.
                    var d = RecvAll(_sock!, len);
                    if (d == null) break;
                    data = d;
                }

                FrameReceived?.Invoke(data);

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

    bool Poll(int timeoutMs)
    {
        try { return _sock!.Poll(timeoutMs * 1000, SelectMode.SelectRead); }
        catch { return false; }
    }

    static byte[]? RecvAll(Socket sock, int n)
    {
        var buf = new byte[n];
        return RecvAllInto(sock, buf, n) ? buf : null;
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

    // PackBits decoder (Apple/TIFF variant).
    // Returns number of bytes written to dst; caller breaks on mismatch with rawBytes.
    static int PackbitsExpand(byte[] src, int srcLen, byte[] dst, int dstOffset, int dstLen)
    {
        int si = 0, di = 0;
        while (si < srcLen && di < dstLen)
        {
            int n = (sbyte)src[si++];
            if (n >= 0)
            {
                int count = n + 1;
                if (si + count > srcLen || di + count > dstLen) break;
                Buffer.BlockCopy(src, si, dst, dstOffset + di, count);
                si += count; di += count;
            }
            else if (n != -128)
            {
                int count = 1 - n;
                if (si >= srcLen || di + count > dstLen) break;
                byte b = src[si++];
                for (int k = 0; k < count; k++) dst[dstOffset + di++] = b;
            }
            // n == -128 (0x80): NOP — skip
        }
        return di;
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
        _sock?.Close();
        _sock = null;
    }
}
