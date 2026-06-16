using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace KronosScreenRemote;

// Persistent-connection ctrl client.
//
// A single TCP connection to port 7374 is established at first use and kept open.
// Commands are written to it directly — no new connection per command, no waiting
// for a response before the next send.  DrainLoop reads all server responses on
// the same socket: OK lines are silently discarded; ERR lines fire OnCtrlError.
//
// One-shot queries (QueryAsync) use a separate short-lived connection so the
// caller gets a direct response without interleaving with the persistent stream.
static class CtrlClient
{
    /// <summary>Fired from a background thread when the daemon sends an ERR response.</summary>
    public static event Action<string>? OnCtrlError;
    public const int CtrlPort = 7374;

    static readonly Channel<string?> _ch =
        Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });

    static string? _pendingMove;    // latest TOUCH_MOVE — Interlocked.Exchange only
    static string? _host;
    static int     _port;

    // Persistent socket.  Written only from SendLoop (single Task) except DropSocket
    // which is also called from DrainLoop.  Reads use Interlocked.CompareExchange.
    static Socket? _sock;

    static CtrlClient() => _ = Task.Run(SendLoop);

    public static void Send(string host, int port, string cmd)
    {
        _host = host;
        _port = port;

        if (cmd.StartsWith("TOUCH_MOVE ", StringComparison.Ordinal))
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

    /// <summary>Drop the persistent connection (e.g. on host change or reconnect).</summary>
    public static void Reset()
    {
        var s = Interlocked.Exchange(ref _sock, null);
        s?.Dispose();
    }

    static async Task SendLoop()
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

    static async Task SendOneAsync(string cmd)
    {
        if (_host is null) return;
        var data = Encoding.ASCII.GetBytes(cmd + "\n");

        var sock = Volatile.Read(ref _sock);
        if (sock != null)
        {
            try { await sock.SendAsync(data, SocketFlags.None); return; }
            catch { DropSocket(sock); }
        }

        sock = await ConnectPersistentAsync();
        if (sock is null) return;

        try { await sock.SendAsync(data, SocketFlags.None); }
        catch { DropSocket(sock); }
    }

    static async Task<Socket?> ConnectPersistentAsync()
    {
        if (_host is null) return null;
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await s.ConnectAsync(_host, _port, cts.Token);
            // Identify this as a persistent session so the server keeps the connection open.
            await s.SendAsync("CTRL_PERSIST\n"u8.ToArray(), SocketFlags.None, cts.Token);

            // Publish before starting the drain loop so SendOneAsync can use it.
            Interlocked.Exchange(ref _sock, s);
            _ = Task.Run(() => DrainLoop(s));
            return s;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ctrl] persistent connect failed: {e.Message}");
            return null;
        }
    }

    // Reads all server responses on the persistent connection.
    // OK lines are silently discarded so the server's send buffer never fills.
    // ERR lines fire OnCtrlError (from this background thread).
    // Exits when the socket closes or errors, then nulls _sock so the next Send reconnects.
    static async Task DrainLoop(Socket sock)
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
                        OnCtrlError?.Invoke(line);
                    // OK and anything else: silently discard
                }
            }
        }
        catch { }
        finally { DropSocket(sock); }
    }

    static void DropSocket(Socket s)
    {
        // Atomically claim ownership; only the first caller disposes.
        if (Interlocked.CompareExchange(ref _sock, null, s) == s)
            s.Dispose();
    }

    public static async Task<string?> QueryAsync(string host, int port, string cmd, int timeoutMs = 2000)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            await s.ConnectAsync(host, port, cts.Token);
            await s.SendAsync(Encoding.ASCII.GetBytes(cmd + "\n"), SocketFlags.None, cts.Token);
            var buf = new byte[32];
            int n = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token);
            return n > 0 ? Encoding.ASCII.GetString(buf, 0, n).Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// One-shot query that accumulates all response lines until the server closes the
    /// connection or an "OK" terminator line is received.  Used for multi-line responses
    /// like SYSINFO that exceed the 32-byte buffer of QueryAsync.
    /// </summary>
    public static async Task<string?> QueryMultiAsync(string host, int port, string cmd, int timeoutMs = 3000)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            await s.ConnectAsync(host, port, cts.Token);
            await s.SendAsync(Encoding.ASCII.GetBytes(cmd + "\n"), SocketFlags.None, cts.Token);
            var sb  = new StringBuilder();
            var buf = new byte[1024];
            while (true)
            {
                int n = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().Contains("\nOK\n")) break;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }
}
