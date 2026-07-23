using System.Net.Sockets;
using System.Text;

namespace KronosScreenRemote;

// Stateless one-shot ctrl-port request/response helpers.
//
// Each call opens its own short-lived TCP connection to (host, port), sends one
// command, reads the reply, and disposes the socket. There is NO shared mutable
// state — every method is a pure function of its arguments — so this is genuinely
// static, unlike the persistent-connection CtrlClient (which owns a socket + send
// loop + the "reset on endpoint change" invariant and is therefore an instance).
//
// Split out of the old static CtrlClient so the two responsibilities no longer
// share a type: persistent stateful sending vs. stateless querying.
static class CtrlQuery
{
    public const int CtrlPort = 7374;

    /// <summary>
    /// Single-line request/response. Accumulates until a full line arrives — a reply
    /// isn't guaranteed to land in one TCP segment / ReceiveAsync call. Null on timeout,
    /// connect failure, or an empty reply.
    /// </summary>
    public static async Task<string?> QueryAsync(string host, int port, string cmd, int timeoutMs = 2000)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            await s.ConnectAsync(host, port, cts.Token);
            await s.SendAsync(Encoding.ASCII.GetBytes(cmd + "\n"), SocketFlags.None, cts.Token);
            var sb  = new StringBuilder();
            var buf = new byte[256];
            while (!sb.ToString().Contains('\n'))
            {
                int n = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// One-shot query that accumulates all response lines until the server closes the
    /// connection or an "OK" terminator line is received. Used for multi-line responses
    /// like SYSINFO that exceed the small buffer of QueryAsync.
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
