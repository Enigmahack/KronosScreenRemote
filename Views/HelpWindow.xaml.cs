using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace KronosScreenRemote;

public partial class HelpWindow : ThemedWindow
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
        HelpViewer.Document = BuildDocument(settings);
    }

    static SolidColorBrush Br(Color c) => new(c);

    static FlowDocument BuildDocument(AppSettings s)
    {
        string K(string action, string fallback = "-")
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

        // Lightweight sub-grouping within one SectionHead - for a feature big enough to need
        // internal structure (Librarian) without implying it's several separate features (which
        // is what another full SectionHead would say). Same idea Touch Calibration's "Observe
        // mode:"/"Warp mode:" lead-ins already used ad hoc; this just names and reuses it.
        Paragraph SubHead(string text)
        {
            var p = new Paragraph { Margin = new Thickness(0, 12, 0, 3) };
            p.Inlines.Add(new Run(text)
            {
                Foreground = Br(CGreen),
                FontSize   = 12,
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

        Add(AppTitle("Kronos ScreenRemote"));
        Add(SubTitle("Stream and control your Korg Kronos synthesizer over a local network."));

        Add(SectionHead("Getting Started"));
        Add(Body("1.  Open Settings (File → Settings...) and enter your Kronos IP address."));
        Add(Body("2.  Use Connection → Connect, or simply launch the app - it attempts to connect automatically."));
        Add(Body("3.  If no credentials are saved, a login dialog appears. Enter the FTP username and password\n" +
                 "    for the Kronos. Check  \"Save password\"  to skip the prompt on future connections.\n" +
                 "    The same credentials are used for both the screen stream and the File Manager."));
        Add(Body("4.  Once connected, the screen panel shows the live Kronos display and the status bar\n" +
                 "    reads  \"Connected - <ip>\"  with a green indicator."));
        Add(Body("5.  If the Kronos IP changes or the connection drops, use Connection → Connect to reconnect.\n" +
                 "    The app does not auto-reconnect after a network interruption."));

        Add(SectionHead("Value Slider  (left panel)"));
        Add(Body("The left panel mirrors the Kronos front-panel VALUE slider and increment/decrement buttons."));
        var vs = ShortcutTable();
        Row(vs, "INC / DEC buttons", "Send a single increment or decrement step to the Kronos.");
        Row(vs, "Slider thumb",      "Drag up or down to send a continuous value (0–127).\n" +
                                     "Top = 127, bottom = 0. The command is sent only when the value changes.");
        Add(vs);
        Add(Note("The left panel is visible in the Full layout when controls are shown. It hides automatically\n" +
                 "in Focused layout or when the value-input panel is hidden via View → Hide Value Input."));

        Add(SectionHead("Screen Panel  (centre)"));
        Add(Body("The screen panel streams the Kronos touchscreen display. The image is scaled to fill the panel " +
                 "while preserving the original 4∶3 aspect ratio."));
        var sp = ShortcutTable();
        Row(sp, "Click",            "Send a tap to the Kronos touchscreen at that position.");
        Row(sp, "Click and drag",   "Send a swipe gesture. Drag must exceed 8 Kronos screen pixels before the touch-down is sent.");
        Row(sp, "Mouse scroll",     "Turn the data wheel  (works from anywhere in the window, not just the screen panel).");
        Row(sp, "Right Click",      "Access the context menu for quick actions.");
        Add(sp);

        Add(SectionHead("Control Surface  (right panel)"));
        Add(Body("The right panel mirrors the physical Kronos front panel. Clicking any button sends the " +
                 "corresponding hardware button press to the Kronos."));
        var cs = ShortcutTable(160);
        Row(cs, "Mode buttons",   "Setlist / Combi / Program / Sequence / Sampling / Global / Disk.\n" +
                                  "The lit (highlighted) button shows the current Kronos operating mode.\n" +
                                  "Click any mode button to switch the Kronos to that mode.");
        Row(cs, "Help / Compare", "Toggle buttons - each click presses the corresponding hardware button.");
        Row(cs, "Number pad",     "Buttons 0–9, dash (–), and dot (.) send numeric entry to the Kronos.");
        Row(cs, "Exit / Enter",   "Send the EXIT or ENTER hardware buttons.");
        Row(cs, "Data wheel",     "Drag up or down to scroll. Mouse scroll wheel also works everywhere.");
        Add(cs);

        Add(SectionHead("Mode Detection  (status bar)"));
        Add(Body("The current Kronos operating mode is read directly from the daemon (its STATE command), " +
                 "which reports the mode from Eva's own live state - exact, no image matching. A few frames " +
                 "after you change mode, the corresponding mode button lights up and the status bar " +
                 "reads  \"Mode: <name>\"."));
        Add(Note("Mode change buttons are disabled until the daemon confirms the board has finished " +
                 "booting (its BOOT= gate), so a stray press during boot can't light the wrong button."));

        Add(SectionHead("Sequencer Transport & Save  (status bar)"));
        Add(Body("The status bar's footer includes a small transport row that sends the Kronos front-panel " +
                 "SEQUENCER buttons, plus a separate Save button just to its left. Both are greyed out - not " +
                 "hidden - when the current Kronos mode doesn't support them, so the row stays in a fixed " +
                 "position instead of the status bar reflowing every time the mode changes."));
        var sq = ShortcutTable(230);
        Row(sq, "Locate / Rewind / Fast-Forward / Pause", "Momentary - send the matching SEQUENCER key.\n" +
                                                            "Active only in Sequence mode.");
        Row(sq, "Record",                                 "Toggle - stays depressed with a pale red background\n" +
                                                            "while armed. Active only in Sequence mode. Stopping\n" +
                                                            "playback clears it automatically (there's no recording-\n" +
                                                            "while-stopped state), and so does any mode change,\n" +
                                                            "as a guard against a stale/desynced toggle.");
        Row(sq, "Start / Stop",                           "Toggle - the icon swaps between ▶ and ■.\n" +
                                                            "Active only in Sequence mode.");
        Row(sq, "Save  (disk icon)",                       "One-shot - sends the same Record/Write key press as\n" +
                                                            "Record, but never toggles. Active in Setlist, Combi,\n" +
                                                            "Program, and Global modes  (the modes where that\n" +
                                                            "key means “Write” rather than “Record”).");
        Add(sq);
        Add(Note("On the real Kronos, REC/WRITE is one physical key - this app splits its two roles onto\n" +
                 "two buttons so both stay visible regardless of the current mode."));

        Add(SectionHead("Keyboard Shortcuts"));
        Add(Body("These shortcuts work when the app window is focused and keyboard capture is not active.\n" +
                 "All shortcuts (except Ctrl combos) can be rebound in File → Settings... → Keybindings."));
        var ks = ShortcutTable(200);
        Row(ks, K("Help",          "F1"),     "Open this help window.");
        Row(ks, K("Mode Setlist",  "F2"),     "Switch Kronos to Setlist mode.");
        Row(ks, K("Mode Combi",    "F3"),     "Switch Kronos to Combi mode.");
        Row(ks, K("Mode Program",  "F4"),     "Switch Kronos to Program mode.");
        Row(ks, K("Mode Sequence", "F5"),     "Switch Kronos to Sequence mode.");
        Row(ks, K("Mode Sampling", "F6"),     "Switch Kronos to Sampling mode.");
        Row(ks, K("Mode Global",   "F7"),     "Switch Kronos to Global mode.");
        Row(ks, K("Mode Disk",     "F8"),     "Switch Kronos to Disk mode.");
        Row(ks, K("Seq Locate",    "-"),      "Sequencer: Locate  (Sequence mode only).");
        Row(ks, K("Seq Rewind",    "-"),      "Sequencer: Rewind  (Sequence mode only).");
        Row(ks, K("Seq Forward",   "-"),      "Sequencer: Fast-Forward  (Sequence mode only).");
        Row(ks, K("Seq Pause",     "-"),      "Sequencer: Pause  (Sequence mode only).");
        Row(ks, K("Seq Record",    "-"),      "Sequencer: Record  (Sequence mode only).");
        Row(ks, K("Seq Start",     "-"),      "Sequencer: Start / Stop  (Sequence mode only).");
        Row(ks, K("Seq Save",      "-"),      "Write / Save current edit  (Setlist/Combi/Program/Global).");
        Row(ks, K("Calibrate",     "C"),      "Toggle touch calibration mode.");
        Row(ks, K("Fullscreen",    "F"),      "Toggle fullscreen.");
        Row(ks, K("Mirror",        "M"),      "Toggle VGA output mirroring on the Kronos.");
        Row(ks, K("Zoom Window",   "Z"),      "Toggle zoom tool over the screen panel.");
        Row(ks, K("HideDataInput",  "-"),     "Hide / show the data input panel  (Full layout only).");
        Row(ks, K("HideValueInput", "-"),     "Hide / show the value input panel  (Full layout only).");
        Row(ks, K("Quit",          "Q"),      "Quit the application.");
        Row(ks, "+  /  −",                    "Zoom in / zoom out  (enables zoom automatically if off).");
        Row(ks, "Esc",                        "Send EXIT to Kronos  (also dismisses overlays and exits fullscreen).");
        Row(ks, "Enter",                      "Send ENTER to Kronos.");
        Row(ks, "Ctrl+1 – Ctrl+5",           "Window size: 75% / 100% / 125% / 150% / 200%.");
        Row(ks, "Ctrl+K",                     "Open the command palette.");
        Row(ks, "Ctrl+Z  /  Ctrl+Y",          "Undo / redo  (calibration mode).");
        Row(ks, "~  (fullscreen only)",       "Show / hide the menu bar while in fullscreen.");
        Add(ks);

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
        Row(ki, "⌨  (blue)",        "Capture active - keystrokes are forwarded to the Kronos.",  CHead);
        Row(ki, "⌨/ (gray slash)",  "Capture inactive - click the screen panel to enable.",       CDim);
        Row(ki, "⌨/ (red slash)",   "Remote typing disabled  (Tools → Disable Remote Typing).",   CRed);
        Add(ki);
        Add(Note("Click outside the screen panel - on the control surface, wheel, or menu bar - to release keyboard capture."));

        Add(SectionHead("Layout Presets  (View → Layout Preset)"));
        var lp = ShortcutTable(100);
        Row(lp, "Full",     "Value slider, screen panel, and control surface side by side (default).");
        Row(lp, "Focused",  "Screen fills the window. A narrow › rail on the right edge can be clicked\n" +
                            "to temporarily overlay the control surface. The value slider is hidden.");
        Add(lp);

        Add(SectionHead("Window Size  (View → Window Size  or  Ctrl+1–5)"));
        Add(Body("Scales the entire window to 75%, 100%, 125%, 150%, or 200%. " +
                 "The value slider, screen panel, and control surface all scale together. Fullscreen overrides this setting."));
        Add(Note("View → Always on Top keeps the window in front of all other applications."));

        Add(SectionHead($"Fullscreen  ({K("Fullscreen", "F")}  or  View → Fullscreen)"));
        Add(Body("Maximises the window with no title bar. The control surface is still accessible " +
                 "in fullscreen (unless the layout preset hides it)."));
        var fs = ShortcutTable(200);
        Row(fs, "~  (tilde)",              "Show or hide the menu bar while in fullscreen.");
        Row(fs, $"{K("Fullscreen", "F")}  or  Esc", "Exit fullscreen and restore the previous window state.");
        Add(fs);

        Add(SectionHead($"Zoom Tool  ({K("Zoom Window", "Z")}  or  View → Zoom Window)"));
        Add(Body("Displays a magnified window that follows the mouse cursor over the screen panel. " +
                 "Press  +  to zoom in and  −  to zoom out in 0.5× steps (range: 2.5× – 10×). " +
                 $"Pressing  +  enables zoom automatically if it is currently off."));

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

        Add(SectionHead("Test Mode  (Tools → Enter Kronos Test Mode)"));
        Add(Body("Sends the Kronos into its built-in hardware test mode. A confirmation dialog warns\n" +
                 "before proceeding - all unsaved changes on the Kronos will be lost, and the Kronos\n" +
                 "must be restarted after testing is complete."));
        Add(Note("Only use this if you understand the risk. This feature is intended for diagnostics\n" +
                 "and hardware verification."));

        Add(SectionHead($"VGA Mirror  ({K("Mirror", "M")}  or  File → Settings...)"));
        Add(Body("Toggles VGA output mirroring on the Kronos. When enabled, the Kronos display is duplicated " +
                 "to the VGA output port. The setting is pushed to the Kronos daemon on every connection."));

        Add(SectionHead("Bank Select  (Bank Select menu  or  rebindable shortcuts)"));
        Add(Body("Sends a bank-select button press to the Kronos. The Bank Select menu is organized into " +
                 "three sub-menus - Internal, User, and User (AA–GG) - rather than one long flat list. " +
                 "Internal and User list single letters A through G and correspond to the internal and user " +
                 "bank rows; User (AA–GG) lists the doubled-letter pairs (AA, BB, ...) and sends a chord of " +
                 "both the U and I buttons simultaneously, selecting the combined user/internal bank slot."));
        Add(Note("Bank select shortcuts are unassigned by default. Bind them in File → Settings... → Keybindings."));

        Add(SectionHead("File Manager  (Tools → File Manager...)"));
        Add(Body("A dual-pane file browser for transferring files between your PC and the Kronos over FTP.\n" +
                 "Uses the same credentials as the screen stream."));
        var fm = ShortcutTable(200);
        Row(fm, "Left pane",                 "Local PC  (starts at the Desktop folder, or your own default -\n" +
                                              "see the note below).");
        Row(fm, "Right pane",                "Kronos filesystem  (/ by default).");
        Row(fm, "\"..\"  (top of either pane)", "Navigate up to the parent folder - same as Backspace or the ↑ button.");
        Row(fm, "Drag left → right",         "Upload files to the Kronos.");
        Row(fm, "Drag right → left",         "Download files to your PC.");
        Row(fm, "Double-click folder",       "Navigate into it.");
        Row(fm, "Backspace",                 "Go up to the parent folder.");
        Row(fm, "F2",                        "Rename the selected item.");
        Row(fm, "F5  /  ↺ button",           "Refresh the active pane.");
        Row(fm, "Del",                       "Delete the selected item.");
        Row(fm, "Ctrl+A",                    "Select all items in the active pane.");
        Add(fm);
        Add(Note("Right-click a folder in the local pane for \"Set Default Start Folder\" - the local pane\n" +
                 "opens there on every future launch. If that folder no longer exists, the local pane falls\n" +
                 "back to C:\\ rather than silently reverting to the Desktop."));
        Add(Note("When a file already exists at the destination, a conflict dialog offers Rename / Overwrite / Skip / Cancel\n" +
                 "with an option to apply the choice to all remaining conflicts."));

        Add(SectionHead("Settings  (File → Settings...)"));
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
        Row(st, "MIDI/SysEx",              "MIDI link (Auto / USB / TCP), monitor toggle, proactive SysEx\n" +
                                           "polling, and the VALUE-slider CC#.");
        Row(st, "Librarian",               "Merge Window staging behavior, duplicate handling, full sync on\n" +
                                           "launch, and force destructive write.");
        Add(st);

        Add(SectionHead("MIDI / SysEx  (status bar + Tools)"));
        Add(Body("The app monitors the Kronos' live MIDI output - program-change and mode-change follow, " +
                 "the VALUE slider mirror, and the SysEx traffic window all run off it. The link can be the " +
                 "daemon's TCP MIDI bridge (port 9875) or a direct USB-MIDI connection to the Kronos - " +
                 "chosen in the MIDI/SysEx tab in Settings (Auto prefers USB). The footer badge shows which link " +
                 "is live:  USB  (green, fast),  DIN  (amber, 5-pin interface),  TCP  (blue, network)."));
        Add(Body("The MIDI Monitor (Tools → MIDI Monitor...) shows live MIDI + SysEx traffic in one list, " +
                 "with per-type filter buttons (click cycles On → Solo → Off) and a virtual piano + pitch " +
                 "joystick for sending notes and pitch bend on the selected OUT CH."));
        var mm = ShortcutTable(200);
        Row(mm, "Click a piano key",          "Play that note on OUT CH  (send Note On, then Note Off on release).");
        Row(mm, "Right-click a piano key",    "Assign a physical keyboard key to that note - press the next\n" +
                                               "key you type. Once assigned, that key plays the note directly.");
        Row(mm, "Drag the pitch joystick",    "Send Pitch Bend directly, proportional to how far you drag.");
        Row(mm, "Right-click the joystick",   "Assign a physical key to \"bend up\" or \"bend down\". Holding\n" +
                                               "an assigned key glides the bend toward that extreme instead of\n" +
                                               "snapping to it, and releasing glides it back to center.");
        Row(mm, "Copy  /  Copy All Shown",    "Copy the selected row(s), or every row currently passing the\n" +
                                               "filters, to the clipboard.");
        Add(mm);
        Add(Note("Assigning a key while the Monitor doesn't have keyboard focus won't see the keypress - " +
                 "click inside the window first. Escape cancels an assignment in progress."));
        Add(Note("If you use a DAW on the same PC, the USB port is exclusive - open the DAW first and " +
                 "this app falls back to TCP, or vice versa."));

        Add(SectionHead("Librarian  (Tools → Librarian...)"));
        Add(Body("The Librarian manages Kronos programs, combis, and set lists: pull everything from the " +
                 "Kronos into a keyboard library, import .pcg files, stage objects in a Merge Window, and " +
                 "place them back onto the instrument with dependency resolution."));
        Add(SubHead("The Panes"));
        Add(Body("• PCG pane: load a .pcg file and pull programs/combis/set lists (transitively - a set " +
                 "list pulls its combis, which pull their programs) into the Merge Window."));
        Add(Body("• Keyboard Library: the on-disk cache synced from the Kronos (Sync Library). Move, edit, " +
                 "and place objects; writes are committed to the Kronos with a Store-Bank step."));
        Add(Body("• Loaded PCG File and Keyboard Library each show a summary line below their tree - " +
                 "counts of Programs/Combis/Drum Kits/Wave Sequences/Set Lists, plus a missing-dependency " +
                 "count that turns red once anything is actually missing."));
        Add(Body("• Merge Window: a staging area. Auto-Fill places everything staged into the next free " +
                 "slots of the right type; dependencies are placed before their referrers so references " +
                 "resolve to where things actually landed."));
        Add(SubHead("Settings  (Librarian tab)"));
        Add(Body("• Merge behavior: Temporary Memory clears the staging when " +
                 "the app closes; Local Storage persists it across sessions. Switching between them takes " +
                 "effect immediately, carrying whatever is already staged across."));
        Add(Body("• Preserve duplicate Programs/Combis  (also on the Merge Window toolbar): when checked, " +
                 "placing staged content that already exists in Keyboard Library " +
                 "still writes a fresh copy; when unchecked, the existing copy is reused instead of writing " +
                 "a duplicate. Defaults: Programs are reused, Combis are copied as-is."));
        Add(Body("• Full sync on launch  (off by default): pulls every bank from " +
                 "the Kronos as soon as the Librarian opens, instead of only the banks whose digest " +
                 "changed. It is a pull ONLY - an action nobody clicked never writes to the instrument, so " +
                 "pending local changes still wait for you to press Sync Library."));
        Add(Body("• Force destructive write  (off by default): normally a bank " +
                 "that changed on the Kronos since this library last pulled it is excluded from the push " +
                 "and flagged as a conflict for you to resolve. With this on, the keyboard library is " +
                 "treated as the source of truth and 2-Way Sync overwrites those changes silently - the " +
                 "standing form of the Resolve Conflicts button. Front-panel edits made since the last pull " +
                 "are lost, and the pre-write backup does NOT cover them - it saves this library's last " +
                 "known copy of each slot, which is exactly what has gone stale. The Librarian shows a red " +
                 "banner for as long as it is armed."));
        Add(SubHead("Working in the Merge Window"));
        Add(Body("• Auto-Fill sends nothing to the Kronos - it only stages, exactly like dragging items " +
                 "across yourself. Review the result, then Commit Changes to push. Anything that doesn't " +
                 "fit (no bank of the matching type has room) stays staged."));
        Add(Body("• Force Overwrite (Merge Window): placing onto a slot another Combi or Set List still " +
                 "references normally refuses, to avoid silently breaking that reference. Force Overwrite " +
                 "places anyway - those referrers then resolve to the NEW object, and the old occupant is " +
                 "diverted to the session clipboard rather than lost."));
        Add(Body("• Object Dependencies: red rows at the top are dependencies nothing staged provides. " +
                 "Right-click one to search a .pcg for it; anything found is staged, so the gap can be " +
                 "filled before you Commit. Below them is every Program/Combi/Drum Kit/Wave Sequence the " +
                 "selection references, nested ones included - double-click a row for more info."));
        Add(Body("• Sample bank names (EXs and 3rd-party) are resolved automatically from the shipped EXs " +
                 "catalog when you load a .pcg. A catalog hit identifies the product, and is not proof " +
                 "the pack is installed on the instrument."));
        Add(Body("• The PCG search box matches name, bank (e.g. 'I-A'), category, EXi engine type (e.g. " +
                 "'AL-1' matches both a name containing it and a Program that IS one), and what the object " +
                 "references. Case-insensitive."));
        Add(SubHead("Good to Know"));
        Add(Body("• Delete and Clear Changes affect the keyboard library only - hardware is untouched " +
                 "until Sync/Commit, and a fresh Pull restores what was deleted. Clear History (bottom-" +
                 "right of the History pane) deletes the local audit log alone: keyboard library, pending " +
                 "edits, and hardware are all unaffected."));
        Add(Body("• Tree dots and tints: a dot marks an object staged or referenced more than once, a blue " +
                 "dot marks a sample reference (see the legend at the bottom of the window), and a " +
                 "conflicted, pending-delete, or read-only row is tinted instead of dotted."));

        Add(SectionHead("Sample Editor  (Tools → Sample Editor...)"));
        Add(Body("View and edit .KSC/.KMP/.KSF sample content directly on disk - key ranges, loop points, " +
                 "sample rate, and destructive DSP edits. Works on a local copy; nothing reaches the " +
                 "Kronos itself from here  (use the File Manager or Librarian to move files)."));
        Add(Body("• Open a .KSC collection via File → Open... (or drag-and-drop) to populate the tree on " +
                 "the left. Selecting a multisample shows its piano keymap and zone list; selecting a zone " +
                 "loads its sample into the waveform view below."));
        Add(Body("• Multisample (MS) panel: the dropdown and Create/Rename/Delete buttons pick which " +
                 "multisample is being edited. Its piano keymap shows the zone layout - click a key to " +
                 "jump to the zone covering it, drag a zone's boundary to resize it, and drag a zone to " +
                 "reorder it."));
        Add(Body("• Zone panel: Index/Sample/Orig. Key/Top Key fields for the selected zone. Create adds " +
                 "an empty zone at the end of the keymap; Import Sample... decodes one or more audio files " +
                 "into the collection and assigns the first to the selected zone."));
        Add(Body("The transport, zoom, and undo/redo controls sit directly above the waveform display, " +
                 "in the same bordered section, so they stay attached to what they control regardless of " +
                 "where the fields above scroll to:"));
        var se = ShortcutTable(220);
        Row(se, "Play / Stop  (Space)",              "Play or stop the loaded sample.");
        Row(se, "Pause",                              "Pause / resume playback.");
        Row(se, "Locate Start / End  (Home / End)",   "Jump the scrub position to the start or end.");
        Row(se, "Rewind / Fast-Forward",              "Step the scrub position back or forward.");
        Row(se, "Zoom In / Out  (Ctrl+ / Ctrl-)",     "Zoom the waveform view, centred on the current view.");
        Row(se, "Zoom to Selection",                  "Fit the highlighted range to the full width.");
        Row(se, "Fit  (Ctrl+0)",                      "Show the whole sample  (also: double-click the waveform).");
        Row(se, "Scroll wheel over waveform",         "Zoom in/out, centred on the cursor - see \"Scroll to\n" +
                                                       "Zoom\" below.");
        Row(se, "Undo / Redo",                        "Step back or forward through waveform edits.");
        Add(se);
        Add(Note("\"Scroll to Zoom\"  (checked by default, next to the zoom buttons): unchecked, the mouse " +
                 "wheel over the waveform just scrolls the pane up/down instead of zooming - the same as " +
                 "scrolling anywhere outside the waveform."));
        Add(Body("• LOCAL EDITS  (orange panel): Select/Move tool toggle, Use Zero  (snap Sample Start/" +
                 "Loop points to the nearest zero-crossing), Loop Lock  (keep Loop Start/End the same " +
                 "length while dragging), and destructive DSP buttons - Normalize, Amplify, Soften, Trim " +
                 "Silence, Reverse, Remove DC Offset, Insert Silence. Each acts on the current selection, " +
                 "or the whole sample when nothing is highlighted. These only ever touch this app's own " +
                 "in-memory buffer and undo stack."));
        Add(Body("• KRONOS  (blue panel): fields that get written into the .KSF and reflected on the " +
                 "hardware once pushed - Loop Enabled and its Sample Start/Loop Start/Loop End/Loop Tune " +
                 "points, plus the Reverse and +12dB Boost sample-level flags."));
        Add(Body("• Save Changes  (bottom right) writes every pending edit to disk - it's greyed out until " +
                 "there's actually something unsaved. Edit → Revert KSC Changes / Revert All Changes " +
                 "discards pending edits instead of saving them."));
        Add(Note("A stereo instrument (two multisamples with a shared name and opposite \"-L\"/\"-R\" " +
                 "suffix) shows both channels stacked in the waveform view, and an edit to one mirrors onto " +
                 "its stereo partner automatically."));

        Add(SectionHead("Command Palette  (Ctrl+K)"));
        Add(Body("A fuzzy-search launcher for all app commands. Start typing to filter; press Enter or click " +
                 "an entry to run it. Useful for infrequently used actions - bank select, layout changes, " +
                 "mirror toggle - without navigating menus."));

        Add(SectionHead("Screenshot  (File menu  or  Ctrl+S)"));
        Add(Body("Saves the current Kronos screen frame as a PNG file. Requires an active connection."));
        var sc = ShortcutTable(240);
        Row(sc, "Save Screenshot...  (Ctrl+S)",  "Shows a save dialog to choose filename and location.");
        Row(sc, "Quick Save Screenshot",        "Saves instantly to the Screenshot Directory (or desktop if unset).");
        Row(sc, "Copy Frame to Clipboard",      "Copies the current frame to the system clipboard.");
        Add(sc);
        Add(Note("Use File → Open Screenshots Folder to browse previously saved files."));

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
        Row(sb, "Sequencer transport + Save", "SEQUENCER buttons and Record/Write - see “Sequencer Transport\n" +
                                               "& Save” above.");
        Row(sb, "Mode: ...",              "Current Kronos operating mode - read from the daemon's STATE command\n" +
                                         "(exact, from Eva's own state) and re-polled every 500 ms.");
        Add(sb);

        return doc;
    }
}
