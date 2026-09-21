namespace KronosScreenRemote;

// Wire values match docs/api.md §4.5 handshake byte 9 (fmt) exactly - do not renumber.
internal enum StreamPixelFormat { Index8 = 0, Rgb565Le = 1 }

internal interface IStreamReceiver : IDisposable
{
    event Action? Disconnected;
    int Width  { get; }
    int Height { get; }
    StreamPixelFormat Fmt { get; }
    int Bpp { get; }   // 8 (Fmt=Index8) or 16 (Fmt=Rgb565Le) - bytes/pixel = Bpp/8
    PaletteEntry[] Palette { get; }  // populated only when Fmt == Index8; empty otherwise
    Task ConnectAsync(CancellationToken ct = default);

    // Copy the latest decoded native-format frame into dst if a new one has arrived since the
    // last call. Returns false and leaves dst untouched when there is no new frame. Thread-safe.
    // dst must be sized Width * Height * Bpp/8 bytes.
    bool TryCopyLatestFrame(byte[] dst);
}
