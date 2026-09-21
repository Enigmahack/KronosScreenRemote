namespace KronosScreenRemote;

// Derived from the daemon's MODEL command FAMILY field (docs/api.md §"MODEL") once per
// connection. Unknown covers "never connected yet" and "MODEL query failed/timed out" - both
// treated the same as Kronos everywhere this matters (currently: MIRROR_ON is skipped on
// Nautilus, which replies ERR MIRROR_UNSUPPORTED there - see MainWindow.Streaming.cs). The
// stream's native pixel format/bpp (StreamReceiver.Fmt/Bpp, INDEX8 on Kronos vs RGB565LE on
// Nautilus) is a separate, protocol-level signal read from the handshake itself, not derived
// from this enum.
public enum DeviceFamily { Unknown, Kronos, Nautilus }
