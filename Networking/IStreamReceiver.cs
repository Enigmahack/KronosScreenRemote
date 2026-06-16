namespace KronosScreenRemote;

internal interface IStreamReceiver : IDisposable
{
    event Action<byte[]>? FrameReceived;
    event Action?         Disconnected;
    int            Width   { get; }
    int            Height  { get; }
    PaletteEntry[] Palette { get; }
    Task ConnectAsync(CancellationToken ct = default);
}
