using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace KronosScreenRemote;

// Persistent-connection ctrl client — ONE instance per endpoint, owned by MainWindow.
//
// A single TCP connection to the ctrl port is established at first use and kept open.
// Commands are written to it directly — no new connection per command, no waiting for a
// response before the next send. DrainLoop reads all server responses on the same socket:
// OK lines are silently discarded; ERR lines fire CtrlError.
//
// The endpoint (host, port) is captured at construction and can't change — the old
// invariant "call Reset() after a host change or the socket targets the old daemon" is
// now structural: a host change means a NEW CtrlClient, and disposing the old one tears
// down its socket + send loop. Reset() remains for dropping the socket on reconnect
// (same endpoint), where the daemon may have closed the persistent session.
//
// One-shot queries (CtrlQuery.QueryAsync / QueryMultiAsync) use separate short-lived
// connections so a caller gets a direct response without interleaving with this stream.
sealed class CtrlClient : ICtrlClient, IDisposable
{
    /// <summary>Fired from a background thread when the daemon sends an ERR response.</summary>
    public event Action<string>? CtrlError;

    readonly string _host;
    readonly int    _port;

    readonly Channel<string?> _ch =
        Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });

    string? _pendingMove;    // latest TOUCH_MOVE — Interlocked.Exchange only

    // Persistent socket. Written only from SendLoop (single Task) except DropSocket
    // which is also called from DrainLoop. Reads use Volatile/Interlocked.
    Socket? _sock;

    public CtrlClient(string host, int port)
    {
        _host = host;
        _port = port;
        _ = Task.Run(SendLoop);
    }

    public void Send(string cmd)
    {
        if (cmd.StartsWith(DaemonCommand.TouchMovePrefix, StringComparison.Ordinal))
        {
            // Coalesce: latest position replaces any not-yet-sent move.
            Interlocked.Exchange(ref _pendingMove, cmd);
            _ch.Writer.TryWrite(null);
        }
        else
        {
            // Non-move command: flush any pending move first to preserve ordering.
            var pm = Interlocked.Exchange(ref _pendingMove, null);
            if (pm != null) _ch.Writer.TryWrite(pm);
            _ch.Writer.TryWrite(cmd);
        }
    }

    /// <summary>Drop the persistent connection (e.g. on reconnect to the same endpoint).</summary>
    public void Reset()
    {
        var s = Interlocked.Exchange(ref _sock, null);
        s?.Dispose();
    }

    /// <summary>One-shot request/response on this instance's endpoint.</summary>
    public Task<string?> QueryAsync(string cmd, int timeoutMs = 2000)
        => CtrlQuery.QueryAsync(_host, _port, cmd, timeoutMs);

    // Stop the send loop and tear down the socket. After Dispose, Send() is a no-op
    // (the completed channel refuses writes).
    public void Dispose()
    {
        _ch.Writer.TryComplete();
        Reset();
    }

    async Task SendLoop()
    {
        await foreach (var item in _ch.Reader.ReadAllAsync())
        {
            string? cmd;
            if (item is null)
            {
                cmd = Interlocked.Exchange(ref _pendingMove, null);
                if (cmd is null) continue;
            }
            else
            {
                cmd = item;
            }

            await SendOneAsync(cmd);
        }
    }

    async Task SendOneAsync(string cmd)
    {
        var data = Encoding.ASCII.GetBytes(cmd + "\n");

        var sock = Volatile.Read(ref _sock);
        if (sock != null)
        {
            // First attempt on the existing socket. A failure here is normal reconnect
            // churn (the daemon closed a stale persistent session) — drop it silently and
            // retry over a fresh connection below.
            try { await sock.SendAsync(data, SocketFlags.None); return; }
            catch { DropSocket(sock); }
        }

        sock = await ConnectPersistentAsync();
        if (sock is null) return;   // connect failure already logged by ConnectPersistentAsync

        // Second attempt, on a socket we JUST connected — a failure here is unexpected and
        // means the command was silently dropped, so surface it (not reconnect noise).
        try { await sock.SendAsync(data, SocketFlags.None); }
        catch (Exception e)
        {
            DropSocket(sock);
            AppLog.Warn($"[ctrl] send dropped after reconnect ({_host}): {e.Message}");
        }
    }

    async Task<Socket?> ConnectPersistentAsync()
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await s.ConnectAsync(_host, _port, cts.Token);
            // Identify this as a persistent session so the server keeps the connection open.
            await s.SendAsync(Encoding.ASCII.GetBytes(DaemonCommand.PersistentSession + "\n"), SocketFlags.None, cts.Token);

            // Publish before starting the drain loop so SendOneAsync can use it.
            Interlocked.Exchange(ref _sock, s);
            _ = Task.Run(() => DrainLoop(s));
            return s;
        }
        catch (Exception e)
        {
            AppLog.Warn($"[ctrl] persistent connect failed ({_host}): {e.Message}");
            return null;
        }
    }

    // Reads all server responses on the persistent connection.
    // OK lines are silently discarded so the server's send buffer never fills.
    // ERR lines fire CtrlError (from this background thread).
    // Exits when the socket closes or errors, then nulls _sock so the next Send reconnects.
    // The daemon's control protocol is newline-delimited OK/ERR lines — a handful of bytes each.
    // Cap the unterminated remainder so a malfunctioning or hostile peer that streams bytes without
    // ever sending a newline can't grow `acc` without bound (memory pressure / OOM); drop it instead.
    const int MaxResponseLine = 64 * 1024;

    async Task DrainLoop(Socket sock)
    {
        var buf = new byte[256];
        var acc = new StringBuilder();
        try
        {
            while (true)
            {
                int n = await sock.ReceiveAsync(buf, SocketFlags.None);
                if (n <= 0) break;
                acc.Append(Encoding.ASCII.GetString(buf, 0, n));

                // Flush all complete lines
                string s;
                int nl;
                while ((nl = (s = acc.ToString()).IndexOf('\n')) >= 0)
                {
                    var line = s[..nl].TrimEnd('\r');
                    acc.Remove(0, nl + 1);
                    if (line.StartsWith("ERR", StringComparison.Ordinal))
                        CtrlError?.Invoke(line);
                    // OK and anything else: silently discard
                }

                // After flushing, only an incomplete (newline-less) tail remains in `acc`.
                // If that tail alone exceeds the cap, the peer is malformed — log and disconnect.
                if (acc.Length > MaxResponseLine)
                {
                    AppLog.Warn($"[ctrl] response line exceeded {MaxResponseLine} bytes from {_host}; dropping malformed peer");
                    break;
                }
            }
        }
        catch { }
        finally { DropSocket(sock); }
    }

    void DropSocket(Socket s)
    {
        // Atomically claim ownership; only the first caller disposes.
        if (Interlocked.CompareExchange(ref _sock, null, s) == s)
            s.Dispose();
    }
}
