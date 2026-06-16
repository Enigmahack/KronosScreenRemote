namespace KronosScreenRemote;

internal interface ICtrlClient
{
    void Send(string cmd);
    void Reset();
    Task<string?> QueryAsync(string cmd, int timeoutMs = 2000);

    /// <summary>Fired (from a background thread) when the daemon sends an ERR response.</summary>
    event Action<string>? CtrlError;
}
