using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace KronosScreenRemote;

public partial class HelpWindow : Window
{
    static readonly Color CBody    = Color.FromRgb(0xC8, 0xC8, 0xC8);
    static readonly Color CHead    = Color.FromRgb(0x88, 0xAA, 0xDD);
    static readonly Color CKey     = Color.FromRgb(0xFF, 0xD2, 0x46);
    static readonly Color CDim     = Color.FromRgb(0x88, 0x88, 0x88);
    static readonly Color CTitle   = Color.FromRgb(0xDD, 0xEE, 0xFF);
    static readonly Color CGreen   = Color.FromRgb(0xAA, 0xCC, 0x88);
    static readonly Color CRed     = Color.FromRgb(0xFF, 0x88, 0x88);

    public HelpWindow(AppSettings settings)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);
        HelpViewer.Document = BuildDocument(settings);
    }

    static SolidColorBrush Br(Color c) => new(c);

    static FlowDocument BuildDocument(AppSettings s)
    {
        string K(string action, string fallback = "—")
        {
            var n = s.GetKeyName(action);
            return string.IsNullOrEmpty(n) ? fallback : n;
        }

        var doc = new FlowDocument
        {
            Background  = Br(Color.FromRgb(0x0E, 0x0E, 0x0E)),
            Foreground  = Br(CBody),
            FontFamily  = new FontFamily("Segoe UI"),
            FontSize    = 13,
            PagePadding = new Thickness(14, 8, 14, 14),
            LineHeight  = 20,
            ColumnWidth = double.MaxValue,
        };

        void Add(Block b) => doc.Blocks.Add(b);

        // ── Heading styles ────────────────────────────────────────────────────

        Paragraph AppTitle(string text)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
            p.Inlines.Add(new Run(text)
            {
                Foreground = Br(CTitle),
                FontSize   = 22,
                FontWeight = FontWeights.Bold,
            });
            return p;
        }

        Paragraph SubTitle(string text)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            p.Inlines.Add(new Run(text) { Foreground = Br(CDim), FontSize = 12 });
            return p;
        }

        Paragraph SectionHead(string text)
        {
            var p = new Paragraph
            {
                Margin          = new Thickness(0, 18, 0, 5),
                BorderBrush     = Br(Color.FromArgb(80, 0x88, 0xAA, 0xDD)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(0, 0, 0, 4),
            };
            p.Inlines.Add(new Run(text)
            {
                Foreground = Br(CHead),
                FontSize   = 14,
                FontWeight = FontWeights.SemiBold,
            });
            return p;
        }

        Paragraph Body(string text, Color? color = null)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };
            p.Inlines.Add(new Run(text) { Foreground = Br(color ?? CBody) });
            return p;
        }

        Paragraph Note(string text) => Body(text, CDim);

        // ── Two-column shortcut table ─────────────────────────────────────────

        Table ShortcutTable(double keyColW = 196)
        {
            var t = new Table
            {
                CellSpacing = 0,
                Margin      = new Thickness(0, 4, 0, 8),
            };
            t.Columns.Add(new TableColumn { Width = new GridLength(keyColW) });
            t.Columns.Add(new TableColumn { Width = new GridLength(keyColW, GridUnitType.Star) });
            t.RowGroups.Add(new TableRowGroup());
            return t;
        }

        void Row(Table t, string key, string desc, Color? keyClr = null)
        {
            var kp = new Paragraph { Margin = new Thickness(0, 1, 8, 1), Padding = new Thickness(0) };
            kp.Inlines.Add(new Run(key)
            {
                Foreground = Br(keyClr ?? CKey),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 12,
            });

            var dp = new Paragraph { Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(0) };
            var lines = desc.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) dp.Inlines.Add(new LineBreak());
                dp.Inlines.Add(new Run(lines[i]) { Foreground = Br(CBody) });
            }

            var row = new TableRow();
            row.Cells.Add(new TableCell(kp) { Padding = new Thickness(0, 0, 8, 0) });
            row.Cells.Add(new TableCell(dp));
            t.RowGroups[0].Rows.Add(row);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DOCUMENT CONTENT
        // ══════════════════════════════════════════════════════════════════════

        // ── Title ─────────────────────────────────────────────────────────────
        Add(AppTitle("Kronos ScreenRemote"));
        Add(SubTitle("Stream and control your Korg Kronos synthesizer over a local network."));

        // ── Getting Started ───────────────────────────────────────────────────
        Add(SectionHead("Getting Started"));
        Add(Body("1.  Open Settings (Settings menu → Settings…) and enter your Kronos IP address."));
        Add(Body("2.  Use Connection → Connect, or simply launch the app — it attempts to connect automatically."));
        Add(Body("3.  If no credentials are saved, a login dialog appears. Enter the FTP username and password\n" +
                 "    for the Kronos. Check  \"Save password\"  to skip the prompt on future connections.\n" +
                 "    The same credentials are used for both the screen stream and the File Manager."));
        Add(Body("4.  Once connected, the screen panel shows the live Kronos display and the status bar\n" +
                 "    reads  \"Connected — <ip>\"  with a green indicator."));
        Add(Body("5.  If the Kronos IP changes or the connection drops, use Connection → Connect to reconnect.\n" +
                 "    The app does not auto-reconnect after a network interruption."));

        // ── Value Slider (left panel) ─────────────────────────────────────────
        Add(SectionHead("Value Slider  (left panel)"));
        Add(Body("The left panel mirrors the Kronos front-panel VALUE slider and increment/decrement buttons."));
        var vs = ShortcutTable();
        Row(vs, "INC / DEC buttons", "Send a single increment or decrement step to the Kronos.");
        Row(vs, "Slider thumb",      "Drag up or down to send a continuous value (0–127).\n" +
                                     "Top = 127, bottom = 0. The command is sent only when the value changes.");
        Add(vs);
        Add(Note("The left panel is visible in the Full layout when controls are shown. It hides automatically\n" +
                 "in Focused layout or when controls are hidden via View → Hide Controls."));

        // ── Screen Panel ──────────────────────────────────────────────────────
        Add(SectionHead("Screen Panel  (centre)"));
        Add(Body("The screen panel streams the Kronos touchscreen display. The image is scaled to fill the panel " +
                 "while optionally preserving the original 4∶3 aspect ratio (A key or View → Aspect Lock)."));
        var sp = ShortcutTable();
        Row(sp, "Click",            "Send a tap to the Kronos touchscreen at that position.");
        Row(sp, "Click and drag",   "Send a swipe gesture. Drag must exceed 8 Kronos screen pixels before the touch-down is sent.");
        Row(sp, "Mouse scroll",     "Turn the data wheel  (works from anywhere in the window, not just the screen panel).");
        Row(sp, "Right Click",      "Access the context menu for quick actions.");
        Add(sp);

        // ── Control Surface ───────────────────────────────────────────────────
        Add(SectionHead("Control Surface  (right panel)"));
        Add(Body("The right panel mirrors the physical Kronos front panel. Clicking any button sends the " +
                 "corresponding hardware button press to the Kronos."));
        var cs = ShortcutTable(160);
        Row(cs, "Mode buttons",   "Setlist / Combi / Program / Sequence / Sampling / Global / Disk.\n" +
                                  "The lit (highlighted) button shows the current Kronos operating mode.\n" +
                                  "Click any mode button to switch the Kronos to that mode.");
        Row(cs, "Help / Compare", "Toggle buttons — each click presses the corresponding hardware button.");
        Row(cs, "Number pad",     "Buttons 0–9, dash (–), and dot (.) send numeric entry to the Kronos.");
        Row(cs, "Exit / Enter",   "Send the EXIT or ENTER hardware buttons.");
        Row(cs, "Data wheel",     "Drag up or down to scroll. Mouse scroll wheel also works everywhere.");
        Add(cs);

        // ── Keyboard Shortcuts ────────────────────────────────────────────────
        Add(SectionHead("Keyboard Shortcuts"));
        Add(Body("These shortcuts work when the app window is focused and keyboard capture is not active.\n" +
                 "All shortcuts (except Ctrl combos) can be rebound in Settings → Settings… → Keybindings."));
        var ks = ShortcutTable(200);
        Row(ks, K("Help",          "F1"),     "Open this help window.");
        Row(ks, K("Mode Setlist",  "F2"),     "Switch Kronos to Setlist mode.");
        Row(ks, K("Mode Combi",    "F3"),     "Switch Kronos to Combi mode.");
        Row(ks, K("Mode Program",  "F4"),     "Switch Kronos to Program mode.");
        Row(ks, K("Mode Sequence", "F5"),     "Switch Kronos to Sequence mode.");
        Row(ks, K("Mode Sampling", "F6"),     "Switch Kronos to Sampling mode.");
        Row(ks, K("Mode Global",   "F7"),     "Switch Kronos to Global mode.");
        Row(ks, K("Mode Disk",     "F8"),     "Switch Kronos to Disk mode.");
        Row(ks, K("AspectLock",    "A"),      "Toggle aspect-ratio lock on the screen panel.");
        Row(ks, K("Calibrate",     "C"),      "Toggle touch calibration mode.");
        Row(ks, K("Fullscreen",    "F"),      "Toggle fullscreen.");
        Row(ks, K("Mirror",        "M"),      "Toggle VGA output mirroring on the Kronos.");
        Row(ks, K("Zoom Window",   "Z"),      "Toggle zoom tool over the screen panel.");
        Row(ks, K("HideControls",  "—"),      "Hide / show the control surface panel  (Full layout only).");
        Row(ks, K("Quit",          "Q"),      "Quit the application.");
        Row(ks, "+  /  −",                    "Zoom in / zoom out  (enables zoom automatically if off).");
        Row(ks, "Esc",                        "Send EXIT to Kronos  (also dismisses overlays and exits fullscreen).");
        Row(ks, "Enter",                      "Send ENTER to Kronos.");
        Row(ks, "Ctrl+1 – Ctrl+5",           "Window size: 75% / 100% / 125% / 150% / 200%.");
        Row(ks, "Ctrl+K",                     "Open the command palette.");
        Row(ks, "Ctrl+Z  /  Ctrl+Y",          "Undo / redo  (calibration mode).");
        Row(ks, "~  (fullscreen only)",       "Show / hide the menu bar while in fullscreen.");
        Add(ks);

        // ── Keyboard Capture ──────────────────────────────────────────────────
        Add(SectionHead("Keyboard Capture  (forwarding keys to the Kronos)"));
        Add(Body("Clicking inside the screen panel activates keyboard capture. " +
                 "While active, most keystrokes are forwarded to the Kronos as if typed on a connected USB keyboard."));
        var kb = ShortcutTable(200);
        Row(kb, "Numpad 0–9",        "Press the matching number pad button on the Kronos control surface.\n" +
                                     "The on-screen button also shows a brief indent for visual confirmation.");
        Row(kb, "Numpad –  /  .",    "Press the NUM_DASH or NUM_DOT control surface buttons.");
        Row(kb, "Numpad Enter",      "Send ENTER to the Kronos.");
        Row(kb, "Escape",            "Send EXIT to the Kronos.");
        Row(kb, "Any other key",     "Forward as a USB keypress to the Kronos kernel input system.");
        Add(kb);
        Add(Body("The ⌨ indicator in the status bar shows capture state:"));
        var ki = ShortcutTable(200);
        Row(ki, "⌨  (blue)",        "Capture active — keystrokes are forwarded to the Kronos.",  CHead);
        Row(ki, "⌨/ (gray slash)",  "Capture inactive — click the screen panel to enable.",       CDim);
        Row(ki, "⌨/ (red slash)",   "Keyboard send disabled  (Tools → Disable Keyboard Send).",   CRed);
        Add(ki);
        Add(Note("Click outside the screen panel — on the control surface, wheel, or menu bar — to release keyboard capture."));

        // ── Layout Presets ────────────────────────────────────────────────────
        Add(SectionHead("Layout Presets  (View → Layout Preset)"));
        var lp = ShortcutTable(100);
        Row(lp, "Full",     "Value slider, screen panel, and control surface side by side (default).");
        Row(lp, "Focused",  "Screen fills the window. A narrow › rail on the right edge can be clicked\n" +
                            "to temporarily overlay the control surface. The value slider is hidden.");
        Add(lp);

        // ── Window Size ───────────────────────────────────────────────────────
        Add(SectionHead("Window Size  (View → Window Size  or  Ctrl+1–5)"));
        Add(Body("Scales the entire window to 75%, 100%, 125%, 150%, or 200%. " +
                 "The value slider, screen panel, and control surface all scale together. Fullscreen overrides this setting."));
        Add(Note("View → Always on Top keeps the window in front of all other applications."));

        // ── Fullscreen ────────────────────────────────────────────────────────
        Add(SectionHead($"Fullscreen  ({K("Fullscreen", "F")}  or  View → Fullscreen)"));
        Add(Body("Maximises the window with no title bar. The control surface is still accessible " +
                 "in fullscreen (unless the layout preset hides it)."));
        var fs = ShortcutTable(200);
        Row(fs, "~  (tilde)",              "Show or hide the menu bar while in fullscreen.");
        Row(fs, $"{K("Fullscreen", "F")}  or  Esc", "Exit fullscreen and restore the previous window state.");
        Add(fs);

        // ── Zoom ──────────────────────────────────────────────────────────────
        Add(SectionHead($"Zoom Tool  ({K("Zoom Window", "Z")}  or  View → Zoom Window)"));
        Add(Body("Displays a magnified window that follows the mouse cursor over the screen panel. " +
                 "Press  +  to zoom in and  −  to zoom out in 0.5× steps (range: 2.5× – 10×). " +
                 $"Pressing  +  enables zoom automatically if it is currently off."));

        // ── Calibration ───────────────────────────────────────────────────────
        Add(SectionHead($"Touch Calibration  ({K("Calibrate", "C")}  or  Tools → Calibration)"));
        Add(Body("Corrects for touchscreen coordinate offset on the Kronos display. " +
                 "Use this if tap positions feel consistently shifted relative to the image. " +
                 "Calibration data is saved automatically and reloaded on the next launch."));
        Add(Body("Calibration has two stages:"));

        Add(Body("Observe mode  (enter with  " + K("Calibrate", "C") + "):"));
        var calO = ShortcutTable(190);
        Row(calO, "Click",               "Send a touch tap to the Kronos. Current calibration will apply to these clicks.");
        Row(calO, "Right-click",         "Add an indicator dot at that position, or remove the nearest existing dot.");
        Row(calO, "W",                   "Enter Warp mode to edit the correction mesh.");
        Row(calO, K("Calibrate", "C"),   "Exit calibration mode.");
        Add(calO);

        Add(Body("Warp mode  (enter from Observe with  W):"));
        var calW = ShortcutTable(190);
        Row(calW, "Drag blue nodes",     "Shift mesh nodes to correct systematic positional offsets.");
        Row(calW, "Right-click",         "Remove the nearest bias dot.");
        Row(calW, "S",                   "Save the mesh to disk.");
        Row(calW, "R",                   "Reset the mesh to identity (no correction, clears offsets).");
        Row(calW, "X",                   "Clear all bias dots.");
        Row(calW, "W",                   "Return to Observe mode.");
        Add(calW);
        Add(Note("Grid size (3×3, 4×4, 5×5) can be changed in Tools → Calibration Grid Size. " +
                 "Changing the grid size clears existing calibration data."));

        // ── Test Mode ─────────────────────────────────────────────────────────
        Add(SectionHead("Test Mode  (Tools → Enter Kronos Test Mode)"));
        Add(Body("Sends the Kronos into its built-in hardware test mode. A confirmation dialog warns\n" +
                 "before proceeding — all unsaved changes on the Kronos will be lost, and the Kronos\n" +
                 "must be restarted after testing is complete."));
        Add(Note("Only use this if you understand the risk. This feature is intended for diagnostics\n" +
                 "and hardware verification."));

        // ── VGA Mirror ────────────────────────────────────────────────────────
        Add(SectionHead($"VGA Mirror  ({K("Mirror", "M")}  or  Settings → Settings…)"));
        Add(Body("Toggles VGA output mirroring on the Kronos. When enabled, the Kronos display is duplicated " +
                 "to the VGA output port. The setting is pushed to the Kronos daemon on every connection."));

        // ── Bank Select ───────────────────────────────────────────────────────
        Add(SectionHead("Bank Select  (Bank Select menu  or  rebindable shortcuts)"));
        Add(Body("Sends a bank-select button press to the Kronos. Banks I-A through I-G and U-A through U-G " +
                 "correspond to the internal and user bank rows. U-XX banks (U-AA, U-BB, …) send a chord of " +
                 "both the U and I buttons simultaneously, selecting the combined user/internal bank slot."));
        Add(Note("Bank select shortcuts are unassigned by default. Bind them in Settings → Settings… → Keybindings."));

        // ── File Manager ──────────────────────────────────────────────────────
        Add(SectionHead("File Manager  (Connection → File Manager)"));
        Add(Body("A dual-pane file browser for transferring files between your PC and the Kronos over FTP.\n" +
                 "Uses the same credentials as the screen stream."));
        var fm = ShortcutTable(200);
        Row(fm, "Left pane",           "Local PC  (starts at the Desktop folder).");
        Row(fm, "Right pane",          "Kronos filesystem  (/ by default).");
        Row(fm, "Drag left → right",   "Upload files to the Kronos.");
        Row(fm, "Drag right → left",   "Download files to your PC.");
        Row(fm, "Double-click folder", "Navigate into it.");
        Row(fm, "Backspace",           "Go up to the parent folder.");
        Row(fm, "F2",                  "Rename the selected item.");
        Row(fm, "F5",                  "Refresh the active pane.");
        Row(fm, "Del",                 "Delete the selected item.");
        Row(fm, "Ctrl+A",              "Select all items in the active pane.");
        Add(fm);
        Add(Note("When a file already exists at the destination, a conflict dialog offers Rename / Overwrite / Skip / Cancel\n" +
                 "with an option to apply the choice to all remaining conflicts."));

        // ── Settings ─────────────────────────────────────────────────────────
        Add(SectionHead("Settings  (Settings → Settings…)"));
        var st = ShortcutTable(200);
        Row(st, "Kronos Host",             "IP address of the Kronos.");
        Row(st, "Stream Port",             "TCP port for the screen stream  (default: 7373).");
        Row(st, "Ctrl Port",               "TCP port for control commands  (default: 7374).");
        Row(st, "Change / Pull mode",      "Change: stream only when the Kronos screen updates (recommended).\n" +
                                           "Pull: poll at a fixed FPS; uses slightly more Kronos CPU. ");
        Row(st, "Max FPS",                 "Frame-rate cap for Pull mode  (1–15 fps).");
        Row(st, "VGA Mirror",              "Enable VGA output mirroring on the Kronos.");
        Row(st, "Screensaver Timeout",     "Seconds before the Kronos display dims  (0 = disabled).");
        Row(st, "Prompt before quitting",  "Show a confirmation dialog when closing the app.");
        Row(st, "Hide Controls",           "Start with the control surface hidden  (Full layout only).");
        Row(st, "Screenshot Directory",    "Default folder for Quick Save screenshots. Empty = save to the desktop.");
        Row(st, "Debug Logging",           "Write verbose diagnostic output to screenremote.log.");
        Row(st, "Zoom Default Level",      "Initial magnification when the zoom window opens  (2.5× – 10×).");
        Row(st, "Zoom Window Size",        "Size of the zoom inset window as a fraction of the frame area.");
        Row(st, "Keybindings",             "Rebind any shortcut listed in the Keyboard Shortcuts section above.");
        Add(st);

        // ── Command Palette ───────────────────────────────────────────────────
        Add(SectionHead("Command Palette  (Ctrl+K)"));
        Add(Body("A fuzzy-search launcher for all app commands. Start typing to filter; press Enter or click " +
                 "an entry to run it. Useful for infrequently used actions — bank select, layout changes, " +
                 "mirror toggle — without navigating menus."));

        // ── Screenshot ────────────────────────────────────────────────────────
        Add(SectionHead("Screenshot  (Tools menu  or  Ctrl+S)"));
        Add(Body("Saves the current Kronos screen frame as a PNG file. Requires an active connection."));
        var sc = ShortcutTable(240);
        Row(sc, "Save Screenshot…  (Ctrl+S)",  "Shows a save dialog to choose filename and location.");
        Row(sc, "Quick Save Screenshot",        "Saves instantly to the Screenshot Directory (or desktop if unset).");
        Row(sc, "Copy Frame to Clipboard",      "Copies the current frame to the system clipboard.");
        Add(sc);
        Add(Note("Use Tools → Open Screenshots Folder to browse previously saved files."));

        // ── Status Bar ────────────────────────────────────────────────────────
        Add(SectionHead("Status Bar"));
        Add(Body("The status bar at the bottom of the window shows:"));
        var sb = ShortcutTable(200);
        Row(sb, "Coloured dot + text",  "Connection state: green = connected, amber = connecting, red = disconnected.");
        Row(sb, "Change / Pull",        "Active streaming mode for the current connection.");
        Row(sb, "N.N fps",              "Measured incoming frame rate while connected.");
        Row(sb, "Open Logs",            "Opens the current ScreenRemote logs.");
        Row(sb, "Keyboard Info",        "Opens a keyboard info pane, displaying various stats related to CPU, Memory, Temperature, and Storage.");
        Row(sb, "VU meter",             "Shows the level of a local Windows audio device (e.g. your DAW output).\n" +
                                        "Click the ▲ button to pick the device to monitor. Choice is saved in settings.");
        Row(sb, "Mode: …",              "Current Kronos operating mode. Detected from the screen image when\n" +
                                         "reference images are available; otherwise polled from the daemon every 1 s.");
        Add(sb);

        return doc;
    }
}
