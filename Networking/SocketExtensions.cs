using System.Net.Sockets;

namespace KronosScreenRemote;

// Socket.SendAsync may legally return fewer bytes than requested (a short send). Every call
// site in this codebase sends small, well-under-one-segment ASCII commands/handshakes and
// assumed one call transmits the whole buffer - in practice the send buffer here is never
// full enough for that to bite, but it costs nothing to close the gap with one shared loop
// instead of trusting the assumption at every call site (CtrlClient, CtrlQuery, StreamReceiver).
static class SocketExtensions
{
    public static async Task SendAllAsync(this Socket sock, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        while (!data.IsEmpty)
        {
            int sent = await sock.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
            if (sent <= 0)
                throw new SocketException((int)SocketError.ConnectionAborted);
            data = data[sent..];
        }
    }
}
