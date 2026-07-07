namespace KronosScreenRemote;

internal interface IStreamReceiver : IDisposable
{
    event Action? Disconnected;
    int            Width   { get; }
    int            Height  { get; }
    PaletteEntry[] Palette { get; }
    Task ConnectAsync(CancellationToken ct = default);

    // Copy the latest decoded frame into dst if a new one has arrived since the last call.
    // Returns false and leaves dst untouched when there is no new frame.  Thread-safe.
    bool TryCopyLatestFrame(byte[] dst);
}
