using System.IO;
using System.Text;

namespace KronosScreenRemote;

static class AppLog
{
    static StreamWriter? _writer;
    static readonly object _lock = new();

    public static string? LogPath    { get; private set; }
    public static bool   DebugEnabled { get; set; } = false;

    // Log of the run BEFORE this one, kept alongside the live log. Init truncates
    // (append: false), so without this the evidence for any crash that takes the
    // process down is destroyed by the very next launch - which is always the first
    // thing anyone does after a crash. Rotating it here costs one file move per
    // start and makes "it crashed, here's the stack" answerable after the fact.
    public static string? PreviousLogPath { get; private set; }

    public static void Init(string path)
    {
        LogPath = path;
        var prev = Path.Combine(Path.GetDirectoryName(path) ?? ".",
            Path.GetFileNameWithoutExtension(path) + ".prev" + Path.GetExtension(path));
        lock (_lock)
        {
            try
            {
                // Move, never copy: DataDir is on the SMB share, the log routinely runs
                // to megabytes, and copying it whole would stall every launch. A rename
                // on the same volume is instant, and Init reopens `path` from scratch
                // (append: false) on the very next line anyway.
                if (File.Exists(path)) { File.Move(path, prev, overwrite: true); PreviousLogPath = prev; }
            }
            catch { /* rotation is best-effort - never block startup over it */ }
            try { _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true }; }
            catch (Exception ex) { Console.Error.WriteLine($"[AppLog] cannot open {path}: {ex.Message}"); }
        }
        Info($"=== KronosScreenRemote v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Info($"[init] log → {path}");
        if (PreviousLogPath != null) Info($"[init] previous run's log kept at {PreviousLogPath}");
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

    // Truncates and reopens the current log file in place - same append:false mode Init
    // uses, so a mid-session clear behaves like a fresh run's log.
    public static void Clear()
    {
        if (LogPath is not string path) return;
        lock (_lock)
        {
            try
            {
                _writer?.Dispose();
                _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            }
            catch (Exception ex) { Console.Error.WriteLine($"[AppLog] cannot clear {path}: {ex.Message}"); }
        }
        Info("=== Log cleared ===");
    }
}
