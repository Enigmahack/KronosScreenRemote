using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace KronosScreenRemote;

// Persistent-connection ctrl client - ONE instance per endpoint, owned by MainWindow.
//
// A single TCP connection to the ctrl port is established at first use and kept open.
// Commands are written to it directly - no new connection per command, no waiting for a
// response before the next send. DrainLoop reads all server responses on the same socket:
// OK lines are silently discarded; ERR lines fire CtrlError.
//
// The endpoint (host, port) is captured at construction and can't change - the old
// invariant "call Reset() after a host change or the socket targets the old daemon" is
// now structural: a host change means a NEW CtrlClient, and disposing the old one tears
// down its socket + send loop. Reset() remains for dropping the socket on reconnect
// (same endpoint), where the daemon may have closed the persistent session.
//
// One-shot queries (CtrlQuery.QueryAsync / QueryMultiAsync) use separate short-lived
// connections so a caller gets a direct response without interleaving with this stream -
// EXCEPT QueryState (STATE), which rides this same persistent connection (see
// QueryStateOverPersistentAsync below). STATE is polled every 500ms for the life of a
// session; a fresh one-shot socket per poll works but the client ends up the active TCP
// closer on every one (it stops reading as soon as it sees the reply line, without waiting
// for the daemon's FIN), which burns through the client's own ephemeral-port table over a
// long session - confirmed in production via repeated WSAENOBUFS/WSAEADDRINUSE from exactly
// this call site. The daemon protocol allows this: STATE can never itself upgrade a fresh
// connection to CTRL_PERSIST (docs/api.md §5.2 "Read-only allowlist exception"), but once a
// connection already IS the persistent session, STATE sent as a later line on it works fine
// (screenremote.c handle_ctrl_persistent_data has no read-only gate). The daemon allows only
// ONE CTRL_PERSIST connection process-wide ("a new CTRL_PERSIST connection replaces any
// previously open persistent connection") - a prior attempt at this fix used a SECOND,
// dedicated persistent connection just for STATE polling, which kept evicting this one and
// broke front-panel button injection. Routing STATE through THIS single connection instead
// avoids that trap entirely.
sealed class CtrlClient : ICtrlClient, IDisposable
{
    /// <summary>Fired from a background thread when the daemon sends an ERR response.</summary>
    public event Action<string>? CtrlError;

    readonly string _host;
    readonly int    _port;

    readonly Channel<string?> _ch =
        Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });

    string? _pendingMove;    // latest TOUCH_MOVE - Interlocked.Exchange only

    // The one in-flight STATE query, if any - Interlocked.Exchange/CompareExchange only.
    // Single slot, not a queue: PollStateAsync only ever awaits one STATE query at a time,
    // and the wire protocol has no request ID to correlate a reply against, so a queue could
    // desync anyway (see QueryStateOverPersistentAsync for why a queue specifically doesn't
    // work here). DrainLoop completes this from the reply's content ("MODE=" prefix), not
    // from queue position.
    TaskCompletionSource<string?>? _pendingStateQuery;

    // ── Lifecycle: guards _generation + _sock together ───────────────────────
    // Reset() bumps _generation and nulls _sock atomically.  ConnectPersistentAsync
    // captures the generation at entry and re-checks after connect; if the generation
    // has moved on, the socket is stale and must be disposed rather than published.
    // This protects both Dispose() AND ordinary Reset() (called from ScreenSession
    // during reconnect/disconnect) against racing with an in-flight connection attempt
    // that started before the reset.
    readonly object _lifecycleLock = new();
    int     _generation;
    Socket? _sock;

    Task? _sendLoopTask;

    public CtrlClient(string host, int port)
    {
        _host = host;
        _port = port;
        _sendLoopTask = Task.Run(SendLoop);
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

    public void Reset()
    {
        Socket? old;
        lock (_lifecycleLock)
        {
            _generation++;
            old = _sock;
            _sock = null;
        }
        old?.Dispose();
        ReleasePendingStateQuery();
    }

    // A dropped/reset/disposed persistent socket means any STATE query still awaiting a
    // reply on it will never get one - release the slot so the next query isn't stuck
    // forever behind a reply that can now never arrive.
    void ReleasePendingStateQuery()
        => Interlocked.Exchange(ref _pendingStateQuery, null)?.TrySetResult(null);

    /// <summary>Request/response on this instance's endpoint. STATE rides the persistent
    /// connection (see class comment); everything else stays one-shot.</summary>
    public Task<string?> QueryAsync(string cmd, int timeoutMs = 2000)
        => cmd == DaemonCommand.QueryState
            ? QueryStateOverPersistentAsync(timeoutMs)
            : CtrlQuery.QueryAsync(_host, _port, cmd, timeoutMs);

    // Sends STATE over the persistent connection (auto-connecting/reconnecting exactly like
    // any other Send()) and waits for DrainLoop to hand back the matching reply.
    //
    // No fallback to a one-shot CtrlQuery.QueryAsync here on purpose: falling back on every
    // failure would double the connection churn during exactly the sustained-failure window
    // this fix exists to eliminate (persistent path fails -> also try one-shot -> both churn).
    // A miss here just means this poll cycle's MODE/EDITCTX display goes stale for one tick,
    // same visible behavior as today's one-shot timeout case - and the underlying persistent
    // connection already self-heals (SendOneAsync reconnects lazily on the next Send/query).
    async Task<string?> QueryStateOverPersistentAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // CompareExchange, not Exchange: if a previous STATE query is still outstanding,
        // skip this cycle rather than replace it - a reply landing after we've moved on
        // would otherwise complete the WRONG (newer) query with stale data. The stuck-slot
        // case (persistent connect never even succeeds, so nothing will ever complete this)
        // is bounded by the timeout below, which releases the slot itself.
        if (Interlocked.CompareExchange(ref _pendingStateQuery, tcs, null) != null)
            return null;

        Send(DaemonCommand.QueryState);

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (winner == tcs.Task) return await tcs.Task.ConfigureAwait(false);

        // Timed out: release the slot (only if it's still ours) so the next cycle isn't
        // permanently blocked behind a reply that may now never arrive.
        Interlocked.CompareExchange(ref _pendingStateQuery, null, tcs);
        return null;
    }

    // Stop the send loop and tear down the socket.
    public void Dispose()
    {
        _ch.Writer.TryComplete();
        // Wait for the send loop to drain and exit.  The generation bump below
        // makes any still-in-flight ConnectPersistentAsync (if the wait times out)
        // self-abort rather than publish a socket we are about to dispose.
        if (_sendLoopTask != null)
        {
            try { _sendLoopTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        Socket? old;
        lock (_lifecycleLock)
        {
            _generation++;
            old = _sock;
            _sock = null;
        }
        old?.Dispose();
        ReleasePendingStateQuery();
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

        Socket? sock;
        lock (_lifecycleLock) sock = _sock;
        if (sock != null)
        {
            // First attempt on the existing socket. A failure here is normal reconnect
            // churn (the daemon closed a stale persistent session) - drop it silently and
            // retry over a fresh connection below.
            try { await sock.SendAllAsync(data); return; }
            catch { DropSocket(sock); }
        }

        sock = await ConnectPersistentAsync();
        if (sock is null) return;

        try { await sock.SendAllAsync(data); }
        catch (Exception e)
        {
            DropSocket(sock);
            AppLog.Warn($"[ctrl] send dropped after reconnect ({_host}): {e.Message}");
        }
    }

    async Task<Socket?> ConnectPersistentAsync()
    {
        int gen;
        lock (_lifecycleLock) gen = _generation;
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await s.ConnectAsync(_host, _port, cts.Token);
            await s.SendAllAsync(Encoding.ASCII.GetBytes(DaemonCommand.PersistentSession + "\n"), cts.Token);

            // Atomically check whether Reset()/Dispose() invalidated this connection
            // while it was in flight.  If the generation moved, the socket belongs to
            // a dead session — dispose it and return null.
            lock (_lifecycleLock)
            {
                if (gen != _generation) { s.Dispose(); return null; }
                _sock = s;
            }
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
    // The daemon's control protocol is newline-delimited OK/ERR lines - a handful of bytes each.
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
                    else if (line.StartsWith("MODE=", StringComparison.Ordinal))
                        // STATE's reply - never "OK"/"ERR", just the data line itself
                        // (see DaemonCommand.QueryState). Dispatched by content, not queue
                        // position - see QueryStateOverPersistentAsync for why. NOTE: if
                        // MODE_DETAIL (daemon docs §7 - also starts its reply with "MODE=")
                        // is ever routed through this connection, it would wrongly satisfy a
                        // pending STATE slot - give it its own prefix check if that happens.
                    {
                        var pending = Interlocked.Exchange(ref _pendingStateQuery, null);
                        // Only the anomaly is worth a line here - STATE polls at 2/sec, so
                        // logging every successful match would bury the sparse [conn]/[WARN]
                        // lines a real diagnosis needs (as this call site's own one-shot
                        // predecessor did for the socket-exhaustion bug this replaces).
                        if (pending is null)
                            AppLog.Debug($"[ctrl] STATE (persistent) reply unmatched (late/duplicate): {line}");
                        pending?.TrySetResult(line);
                    }
                    // OK and anything else: silently discard
                }

                // After flushing, only an incomplete (newline-less) tail remains in `acc`.
                // If that tail alone exceeds the cap, the peer is malformed - log and disconnect.
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
        // Atomically claim ownership; only the owner clears the field.
        lock (_lifecycleLock)
        {
            if (_sock == s) _sock = null;
        }
        s.Dispose();
        ReleasePendingStateQuery();
    }
}
