using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace KronosScreenRemote;

public readonly record struct Keybind(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public static readonly Keybind None = new(Key.None);

    public string ToDisplayString()
    {
        if (Key == Key.None) return "(none)";
        var parts = new System.Collections.Generic.List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyLabel(Key));
        return string.Join("+", parts);
    }

    static string KeyLabel(Key key)
    {
        var name = key.ToString();
        // WPF names D0–D9 for the digit row; show just the digit
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
            return name[1..];
        return name;
    }

    public string Serialize() => Key == Key.None ? "None" : ToDisplayString();

    public static Keybind Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "None") return None;
        var mods = ModifierKeys.None;
        Key? key  = null;
        foreach (var part in s.Split('+'))
        {
            switch (part.Trim())
            {
                case "Ctrl":  mods |= ModifierKeys.Control; break;
                case "Alt":   mods |= ModifierKeys.Alt;     break;
                case "Shift": mods |= ModifierKeys.Shift;   break;
                case "Win":   mods |= ModifierKeys.Windows; break;
                default:
                    var token = part.Trim();
                    // "1"–"9"/"0" → Key.D1...Key.D0 (reverse of KeyLabel)
                    if (token.Length == 1 && char.IsDigit(token[0]))
                        token = "D" + token;
                    if (System.Enum.TryParse<Key>(token, out var k) && k != Key.None)
                        key = k;
                    break;
            }
        }
        return key.HasValue ? new Keybind(key.Value, mods) : None;
    }
}

record struct PaletteEntry(byte R, byte G, byte B);
record struct CalBiasDot(int Nx, int Ny);

enum CalHistKind { NodeMove, DotAdded, DotRemoved }

record struct CalHistEntry(
    CalHistKind Kind,
    int Col = 0, int Row = 0,
    int OldOffX = 0, int OldOffY = 0,
    int NewOffX = 0, int NewOffY = 0,
    int DotIdx = -1,
    CalBiasDot Dot = default);

sealed class CalMesh
{
    public int Cols { get; }
    public int Rows { get; }

    readonly int[,] _offX;
    readonly int[,] _offY;

    public CalMesh(int cols = 5, int rows = 5)
    {
        Cols = cols;
        Rows = rows;
        _offX = new int[cols, rows];
        _offY = new int[cols, rows];
    }

    public int NatX(int col, int w) =>
        (int)Math.Round(col * (w - 1.0) / (Cols - 1));

    public int NatY(int row, int h) =>
        (int)Math.Round(row * (h - 1.0) / (Rows - 1));

    public (int offX, int offY) GetOffset(int col, int row) =>
        (_offX[col, row], _offY[col, row]);

    public void SetOffset(int col, int row, int offX, int offY)
    {
        if (col < 0 || col >= Cols || row < 0 || row >= Rows) return;
        _offX[col, row] = offX;
        _offY[col, row] = offY;
    }

    public (int x, int y) NodeDst(int col, int row, int w, int h) =>
        (NatX(col, w) + _offX[col, row],
         NatY(row, h) + _offY[col, row]);

    // ── Forward map: natural coords → warped coords ───────────────────────────

    public (int x, int y) Apply(int px, int py, int w, int h)
    {
        var (rx, ry) = ApplyF(px, py, w, h);
        return (Math.Clamp((int)Math.Round(rx), 0, w - 1),
                Math.Clamp((int)Math.Round(ry), 0, h - 1));
    }

    (double fx, double fy) ApplyF(double px, double py, int w, int h)
    {
        double cellW = (w - 1.0) / (Cols - 1);
        double cellH = (h - 1.0) / (Rows - 1);

        int col = Math.Clamp((int)(px / cellW), 0, Cols - 2);
        int row = Math.Clamp((int)(py / cellH), 0, Rows - 2);

        double tx = Math.Clamp((px - col * cellW) / cellW, 0, 1);
        double ty = Math.Clamp((py - row * cellH) / cellH, 0, 1);

        var (tlx, tly) = NodeDst(col,     row,     w, h);
        var (trx, ry0) = NodeDst(col + 1, row,     w, h);
        var (blx, bly) = NodeDst(col,     row + 1, w, h);
        var (brx, bry) = NodeDst(col + 1, row + 1, w, h);

        return (Bilerp(tlx, trx, blx, brx, tx, ty),
                Bilerp(tly, ry0, bly, bry, tx, ty));
    }

    // ── Inverse map: warped coords → natural coords ───────────────────────────

    public (int x, int y) InverseApply(int px, int py, int w, int h)
    {
        double x = px, y = py;
        for (int i = 0; i < 16; i++)
        {
            var (fx, fy) = ApplyF(x, y, w, h);
            double dx = px - fx, dy = py - fy;
            x = Math.Clamp(x + dx, 0, w - 1);
            y = Math.Clamp(y + dy, 0, h - 1);
            if (dx * dx + dy * dy < 0.01) break;
        }
        return (Math.Clamp((int)Math.Round(x), 0, w - 1),
                Math.Clamp((int)Math.Round(y), 0, h - 1));
    }

    static double Bilerp(double tl, double tr, double bl, double br, double tx, double ty)
        => Lerp(Lerp(tl, tr, tx), Lerp(bl, br, tx), ty);

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public void Reset()
    {
        Array.Clear(_offX);
        Array.Clear(_offY);
    }

    public bool IsIdentity()
    {
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
                if (_offX[c, r] != 0 || _offY[c, r] != 0) return false;
        return true;
    }
}

static class WindowTheme
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const int  GWL_STYLE        = -16;
    const int  WS_MINIMIZEBOX   = 0x00020000;
    const uint SWP_NOSIZE       = 0x0001;
    const uint SWP_NOMOVE       = 0x0002;
    const uint SWP_NOZORDER     = 0x0004;
    const uint SWP_FRAMECHANGED = 0x0020;

    // Match title bar to status bar background (#1A1A1A). DWMWA_CAPTION_COLOR (35)
    // requires Windows 11 build 22000+. COLORREF is 0x00BBGGRR.
    public static void ApplyDarkCaption(Window w)
    {
        w.Loaded += (_, _) =>
        {
            int c = 0x001A1A1A;
            var h = new WindowInteropHelper(w).Handle;
            DwmSetWindowAttribute(h, 35, ref c, sizeof(int));
        };
    }

    // Clear WS_MINIMIZEBOX so the window can't be minimized (its minimize button greys out,
    // maximize/resize is untouched). Runs at SourceInitialized - the earliest point the HWND
    // exists - so the button never flashes enabled before the window is shown. Applied by
    // ThemedWindow to every window except the ones that opt in via AllowMinimize (MainWindow).
    public static void DisableMinimizeBox(Window w)
    {
        static void Apply(Window win)
        {
            var h = new WindowInteropHelper(win).Handle;
            if (h == IntPtr.Zero) return;
            int style = GetWindowLong(h, GWL_STYLE);
            SetWindowLong(h, GWL_STYLE, style & ~WS_MINIMIZEBOX);
            // Force the non-client frame to redraw so the style change actually takes effect -
            // SetWindowLong alone is silently ignored often enough that this nudge is required.
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply(w);
        else w.SourceInitialized += (_, _) => Apply(w);
    }
}
