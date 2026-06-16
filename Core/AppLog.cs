using System.IO;
using System.Text;

namespace KronosScreenRemote;

static class AppLog
{
    static StreamWriter? _writer;
    static readonly object _lock = new();

    public static string? LogPath    { get; private set; }
    public static bool   DebugEnabled { get; set; } = false;

    public static void Init(string path)
    {
        LogPath = path;
        lock (_lock)
        {
            try { _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true }; }
            catch (Exception ex) { Console.Error.WriteLine($"[AppLog] cannot open {path}: {ex.Message}"); }
        }
        Info($"=== KronosScreenRemote v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Info($"[init] log → {path}");
    }

    public static void Info (string msg) => Write("INFO ", msg, toConsole: true);
    public static void Warn (string msg) => Write("WARN ", msg, toConsole: true);
    public static void Error(string msg) => Write("ERROR", msg, toConsole: true);

    // Debug only goes to the log file (not console) to avoid polluting stdout
    public static void Debug(string msg) { if (DebugEnabled) Write("DEBUG", msg, toConsole: false); }

    static void Write(string level, string msg, bool toConsole)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (_lock)
        {
            if (toConsole) Console.WriteLine(line);
            try { _writer?.WriteLine(line); }
            catch { /* log write failure is non-fatal */ }
        }
    }

    public static void Close()
    {
        Info("=== KronosScreenRemote closing ===");
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }
}
