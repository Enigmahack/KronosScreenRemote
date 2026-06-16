namespace KronosScreenRemote;

// Thin wrapper giving MainWindow an injectable ICtrlClient backed by the static CtrlClient.
// Captures host and port at construction; recreate the adapter when the endpoint changes.
internal sealed class CtrlClientAdapter(string host, int port) : ICtrlClient
{
    public void Send(string cmd) => CtrlClient.Send(host, port, cmd);
    public void Reset()          => CtrlClient.Reset();
    public Task<string?> QueryAsync(string cmd, int timeoutMs = 2000)
        => CtrlClient.QueryAsync(host, port, cmd, timeoutMs);

    // Forward ICtrlClient.CtrlError to the underlying static event.
    public event Action<string>? CtrlError
    {
        add    => CtrlClient.OnCtrlError += value;
        remove => CtrlClient.OnCtrlError -= value;
    }
}
