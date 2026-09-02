using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static KronosScreenRemote.ThemeBrushes;

namespace KronosScreenRemote;

enum FilterState { On, Filter, Off }

enum MidiMsgType { Note, CC, ProgramChange, PitchBend, AfterTouch, SysEx, Transport, Other }

partial class SysExToolWindow : ThemedWindow
{
    const int MaxEntries = 1000;

    // White key stride in px; keys are this wide minus a 1 px gap.
    const double WW = 28;
    const double WH = 148;
    const double BW = 16;
    const double BH = 92;

    static readonly Brush WhiteNormal  = Frozen(0xDC, 0xDC, 0xDC);
    static readonly Brush WhiteHover   = Frozen(0xF0, 0xF0, 0xF0);
    static readonly Brush WhitePressed = Frozen(0x90, 0xB8, 0xFF);
    static readonly Brush BlackNormal  = Frozen(0x1E, 0x1E, 0x1E);
    static readonly Brush BlackHover   = Frozen(0x32, 0x32, 0x32);
    static readonly Brush BlackPressed = Frozen(0x18, 0x38, 0x68);
    static readonly Brush KeyBorder    = Frozen(0x44, 0x44, 0x44);

    // Filter button color sets: (background, border, foreground)
    static readonly (Brush Bg, Brush Border, Brush Fg) StyleOn =
        (Frozen(0x1B, 0x3A, 0x1B), Frozen(0x3A, 0x7A, 0x3A), Frozen(0x7D, 0xC9, 0x7D));
    static readonly (Brush Bg, Brush Border, Brush Fg) StyleFilter =
        (Frozen(0x3A, 0x30, 0x00), Frozen(0x7A, 0x64, 0x00), Frozen(0xCC, 0xAA, 0x33));
    static readonly (Brush Bg, Brush Border, Brush Fg) StyleOff =
        (Frozen(0x2A, 0x15, 0x15), Frozen(0x6E, 0x2E, 0x2E), Frozen(0xCC, 0x66, 0x66));

    readonly IRawMidiSend _sysEx;
    readonly ObservableCollection<SysExMessageItem> _allItems = new();
    ICollectionView _view = null!;

    // Live traffic is enqueued from background threads (the MIDI consumer thread and
    // TX callers) and drained onto the UI thread in batches by _flushTimer. This is
    // what keeps a burst - a Set List sync streams many large objects - from flooding
    // the dispatcher with one InvokeAsync + collection mutation + scroll per message,
    // which stalled every window (they all share the one UI thread). The display row,
    // including its hex decode, is built on the enqueuing thread so even that work
    // stays off the UI thread; the flush only mutates the collection and scrolls once.
    readonly ConcurrentQueue<(SysExMessageItem Item, byte[]? Raw, bool IsMidi, bool IsSend)> _incoming = new();
    readonly DispatcherTimer _flushTimer =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };

    readonly Dictionary<MidiMsgType, FilterState> _filterStates = new()
    {
        [MidiMsgType.Note]          = FilterState.On,
        [MidiMsgType.CC]            = FilterState.On,
        [MidiMsgType.ProgramChange] = FilterState.On,
        [MidiMsgType.PitchBend]     = FilterState.On,
        [MidiMsgType.AfterTouch]    = FilterState.On,
        [MidiMsgType.SysEx]         = FilterState.On,
        [MidiMsgType.Transport]     = FilterState.On,
    };

    Border? _pressedKey;
    int     _pressedNote = -1;

    // MIDI-lit keys: notes currently lit by incoming NoteOn from the Kronos.
    readonly HashSet<int> _midiLitNotes = new();
    // Map from MIDI note to its piano key Border, for O(1) lighting updates.
    readonly Dictionary<int, Border> _keyByMidi = new();

    // Physical-key -> note assignments (right-click a piano key to set), and the
    // capture state while waiting for the next keypress to bind. Only one capture
    // (piano key or joystick direction) can be pending at a time.
    readonly Dictionary<Key, int> _keyAssignments = new();
    (Action<Key> Assign, Action End)? _pendingCapture;

    // Joystick: _jsValue is the live -1..+1 position; _jsTimer glides it toward
    // whichever of _jsUpKey/_jsDownKey is held (or 0, spring-centered, otherwise).
    // Mouse drag sets _jsValue directly - only key-driven motion is interpolated,
    // per spec ("glides to note instead of snaps" when assigned to keys).
    double _jsValue;
    bool   _jsDragging;
    int    _jsLastSentBend = 8192;   // center; avoids resending an unchanged bend every tick
    Key?   _jsUpKey, _jsDownKey;
    bool   _jsUpHeld, _jsDownHeld;
    readonly DispatcherTimer _jsTimer =
        new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    // Pitch is for general control, not precision, so the actual MIDI Pitch Bend
    // sends are capped to 15 Hz - decoupled from the 60fps glide above, which only
    // drives the puck position and readout. Same producer/flush split as _incoming/_flushTimer.
    readonly DispatcherTimer _jsSendTimer =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000.0 / 15) };

    public int SelectedChannel => CMB_OutChannel.SelectedIndex >= 0 ? CMB_OutChannel.SelectedIndex + 1 : 1;

    public SysExToolWindow(IRawMidiSend sysEx, int initialChannel = 1)
    {
        _sysEx = sysEx;
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_allItems);
        _view.Filter = FilterMessage;
        LB_All.ItemsSource = _view;

        BTN_Clear.Click += (_, _) => Clear();

        MNU_CopyLine.Click += (_, _) => CopySelected();
        MNU_CopyAll.Click  += (_, _) => CopyAllShown();
        LB_All.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CopySelected();
                e.Handled = true;
            }
        };

        InitFilterButton(BTN_Filter_Notes,      MidiMsgType.Note);
        InitFilterButton(BTN_Filter_CC,         MidiMsgType.CC);
        InitFilterButton(BTN_Filter_Prog,       MidiMsgType.ProgramChange);
        InitFilterButton(BTN_Filter_SysEx,      MidiMsgType.SysEx);
        InitFilterButton(BTN_Filter_Bend,       MidiMsgType.PitchBend);
        InitFilterButton(BTN_Filter_AfterTouch, MidiMsgType.AfterTouch);
        InitFilterButton(BTN_Filter_Transport,  MidiMsgType.Transport);

        for (int ch = 1; ch <= 16; ch++)
            CMB_OutChannel.Items.Add($"CH {ch}");
        CMB_OutChannel.SelectedIndex = Math.Clamp(initialChannel - 1, 0, 15);
        CMB_OutChannel.SelectionChanged += (_, _) => ClearMidiLitKeys();

        _sysEx.SysExTraffic += OnTraffic;
        _flushTimer.Tick += FlushIncoming;
        _flushTimer.Start();

        JoystickTrack.MouseLeftButtonDown  += OnJoyMouseDown;
        JoystickTrack.MouseMove            += OnJoyMouseMove;
        JoystickTrack.MouseLeftButtonUp    += OnJoyMouseUp;
        JoystickTrack.MouseRightButtonDown += OnJoystickRightClick;
        _jsTimer.Tick += OnJoystickTick;
        _jsTimer.Start();
        _jsSendTimer.Tick += (_, _) => FlushJoystickSend();
        _jsSendTimer.Start();

        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp   += OnWindowPreviewKeyUp;

        // A key assigned to a note or a joystick direction only gets its key-up while
        // this window has focus. Alt-tabbing or clicking another app away mid-press
        // means that key-up never arrives here, which otherwise leaves the note or the
        // joystick latched on forever. Release everything the moment focus leaves.
        Deactivated += (_, _) =>
        {
            ReleaseNote();
            _jsUpHeld = false;
            _jsDownHeld = false;
            if (_pendingCapture is { } pc) { pc.End(); _pendingCapture = null; }
        };

        Closed += (_, _) =>
        {
            _sysEx.SysExTraffic -= OnTraffic;
            _flushTimer.Stop();
            _flushTimer.Tick -= FlushIncoming;
            ReleaseNote();
            ClearMidiLitKeys();

            _jsTimer.Stop();
            _jsTimer.Tick -= OnJoystickTick;
            _jsSendTimer.Stop();
            if (_jsLastSentBend != 8192)
            {
                int ch = SelectedChannel - 1;
                _ = _sysEx.SendMidiAsync($"{0xE0 | ch:X2} 00 40");   // center the bend on close
            }
        };

        Loaded += (_, _) =>
        {
            BuildPiano();
            BuildJoystick();
        };
    }

    // Show which MIDI link this monitor's traffic is flowing over (USB / DIN / TCP),
    // pushed by MainWindow as the active transport changes. UI thread only.
    public void SetActiveStream(string? label)
    {
        TXT_Stream.Text = $"Stream: {(string.IsNullOrWhiteSpace(label) ? "-" : label)}";
    }

    void ClearMidiLitKeys()
    {
        foreach (int note in _midiLitNotes)
        {
            if (_keyByMidi.TryGetValue(note, out var key) && note != _pressedNote)
            {
                var (_, isBlack) = ((int, bool))key.Tag!;
                key.Background = isBlack ? BlackNormal : WhiteNormal;
            }
        }
        _midiLitNotes.Clear();
    }

    void InitFilterButton(Button btn, MidiMsgType type)
    {
        ApplyFilterStyle(btn, _filterStates[type]);
        btn.Click += (_, _) => CycleFilter(type, btn);
    }

    void CycleFilter(MidiMsgType type, Button btn)
    {
        _filterStates[type] = _filterStates[type] switch
        {
            FilterState.On     => FilterState.Filter,
            FilterState.Filter => FilterState.Off,
            _                  => FilterState.On,
        };
        ApplyFilterStyle(btn, _filterStates[type]);
        _view.Refresh();
    }

    static void ApplyFilterStyle(Button btn, FilterState state)
    {
        var (bg, border, fg) = state switch
        {
            FilterState.On     => StyleOn,
            FilterState.Filter => StyleFilter,
            _                  => StyleOff,
        };
        btn.Background  = bg;
        btn.BorderBrush = border;
        btn.Foreground  = fg;
    }

    bool FilterMessage(object obj)
    {
        if (obj is not SysExMessageItem item) return false;

        bool anySolo = _filterStates.Values.Any(s => s == FilterState.Filter);
        if (anySolo)
            return _filterStates.TryGetValue(item.MsgType, out var fs) && fs == FilterState.Filter;

        return !_filterStates.TryGetValue(item.MsgType, out var s) || s != FilterState.Off;
    }

    void BuildPiano()
    {
        PianoCanvas.Height = WH;

        int[] whiteSemitones = [0, 2, 4, 5, 7, 9, 11];
        (int leftWhite, int semitone)[] blackKeys =
            [(0, 1), (1, 3), (3, 6), (4, 8), (5, 10)];

        var whites = new List<(double x, int midi)>();
        var blacks = new List<(double x, int midi)>();

        const int startNote   = 21;
        const int startOffset = 3;

        whites.Add((0, 21));
        blacks.Add((18, 22));
        whites.Add((28, 23));

        for (int oct = 0; oct < 7; oct++)
        {
            int wBase = oct * 7;
            int mBase = oct * 12;

            for (int i = 0; i < whiteSemitones.Length; i++)
                whites.Add(((wBase + i + 1 + (startOffset / 2)) * WW,
                             startNote + startOffset + mBase + whiteSemitones[i]));

            foreach (var (lw, st) in blackKeys)
                blacks.Add(((wBase + lw + 2 + (startOffset / 2)) * WW - BW / 2.0,
                             startNote + startOffset + mBase + st));
        }

        whites.Add((1428, 108));

        foreach (var (x, midi) in whites) AddKey(x, WW - 1, WH, false, midi);
        foreach (var (x, midi) in blacks) AddKey(x, BW, BH, true, midi);
    }

    void AddKey(double x, double w, double h, bool isBlack, int midi)
    {
        var key = new Border
        {
            Width           = w,
            Height          = h,
            Background      = isBlack ? BlackNormal : WhiteNormal,
            BorderBrush     = isBlack ? null : KeyBorder,
            BorderThickness = isBlack ? default : new Thickness(1, 1, 1, 0),
            CornerRadius    = new CornerRadius(0, 0, isBlack ? 2 : 3, isBlack ? 2 : 3),
            Cursor          = Cursors.Hand,
            Tag             = (midi, isBlack),
        };

        Canvas.SetLeft(key, x);
        Canvas.SetTop(key, 0);
        Panel.SetZIndex(key, isBlack ? 1 : 0);

        key.MouseEnter += OnKeyEnter;
        key.MouseLeave += OnKeyLeave;
        key.MouseDown  += OnKeyDown;
        key.MouseUp    += OnKeyUp;
        key.MouseRightButtonDown += OnKeyRightClick;

        PianoCanvas.Children.Add(key);
        _keyByMidi[midi] = key;
    }

    void OnKeyEnter(object sender, MouseEventArgs e)
    {
        var key = (Border)sender;
        var (midi, isBlack) = ((int, bool))key.Tag!;
        if (key != _pressedKey) key.Background = isBlack ? BlackHover : WhiteHover;
        TXT_NoteLabel.Text = NoteName(midi);
    }

    void OnKeyLeave(object sender, MouseEventArgs e)
    {
        var key = (Border)sender;
        var (midi, isBlack) = ((int, bool))key.Tag!;
        if (key != _pressedKey && !_midiLitNotes.Contains(midi))
            key.Background = isBlack ? BlackNormal : WhiteNormal;
        if (TXT_NoteLabel.Text == NoteName(midi) && _pressedNote < 0)
            TXT_NoteLabel.Text = "";
    }

    void OnKeyDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var key = (Border)sender;
        var (midi, _) = ((int, bool))key.Tag!;
        PressNote(key, midi);
        key.CaptureMouse();
        e.Handled = true;
    }

    void OnKeyRightClick(object sender, MouseButtonEventArgs e)
    {
        var key = (Border)sender;
        var (midi, isBlack) = ((int, bool))key.Tag!;
        var existing = _keyAssignments.Where(kv => kv.Value == midi).Select(kv => (Key?)kv.Key).FirstOrDefault();

        var menu = new ContextMenu();
        var miAssign = new MenuItem
        {
            Header = existing.HasValue ? $"Reassign Key (currently {existing})…" : "Assign Physical Key…"
        };
        miAssign.Click += (_, _) => BeginKeyCapture(
            k => _keyAssignments[k] = midi,
            onBegin: () => key.Background = StyleFilter.Bg,
            onEnd:   () => key.Background = isBlack ? BlackNormal : WhiteNormal);
        menu.Items.Add(miAssign);

        if (existing.HasValue)
        {
            var miClear = new MenuItem { Header = $"Clear Assignment ({existing})" };
            miClear.Click += (_, _) => _keyAssignments.Remove(existing.Value);
            menu.Items.Add(miClear);
        }

        menu.PlacementTarget = key;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // Common press path for both mouse and physical-key-assigned playback. Mouse
    // capture (for drag-off release) is applied by the mouse caller only.
    void PressNote(Border key, int midi)
    {
        var (_, isBlack) = ((int, bool))key.Tag!;
        ReleaseNote();

        _pressedKey  = key;
        _pressedNote = midi;
        key.Background = isBlack ? BlackPressed : WhitePressed;

        int ch = SelectedChannel - 1;
        _ = _sysEx.SendMidiAsync($"{0x90 | ch:X2} {midi:X2} 64");
        TXT_NoteLabel.Text = NoteName(midi);
    }

    // A key/joystick-direction capture waiting for the next physical keypress.
    // Escape cancels without assigning; either way onEnd restores the "listening"
    // highlight applied by onBegin.
    void BeginKeyCapture(Action<Key> assign, Action onBegin, Action onEnd)
    {
        onBegin();
        _pendingCapture = (assign, onEnd);
    }

    static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt  or Key.RightAlt  or Key.LWin      or Key.RWin       or Key.System;

    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_pendingCapture is { } pc)
        {
            if (IsModifierKey(key)) return;   // wait for the real key, not the modifier that precedes it
            _pendingCapture = null;
            if (key != Key.Escape) pc.Assign(key);
            pc.End();
            e.Handled = true;
            return;
        }

        // Don't steal ordinary shortcuts (Ctrl+C, etc.) from assigned keys.
        if (Keyboard.Modifiers != ModifierKeys.None || e.IsRepeat) return;

        if (_jsUpKey == key)   { _jsUpHeld   = true; e.Handled = true; return; }
        if (_jsDownKey == key) { _jsDownHeld = true; e.Handled = true; return; }

        if (_keyAssignments.TryGetValue(key, out int midi) && _keyByMidi.TryGetValue(midi, out var border))
        {
            PressNote(border, midi);
            e.Handled = true;
        }
    }

    void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_jsUpKey == key)   { _jsUpHeld   = false; e.Handled = true; return; }
        if (_jsDownKey == key) { _jsDownHeld = false; e.Handled = true; return; }

        if (_keyAssignments.TryGetValue(key, out int midi) && _pressedNote == midi)
        {
            ReleaseNote();
            e.Handled = true;
        }
    }

    void OnKeyUp(object sender, MouseButtonEventArgs e)
    {
        var key = (Border)sender;
        key.ReleaseMouseCapture();

        var (midi, isBlack) = ((int, bool))key.Tag!;
        ReleaseNote();

        if (!_midiLitNotes.Contains(midi))
        {
            key.Background = Mouse.DirectlyOver == key
                ? (isBlack ? BlackHover : WhiteHover)
                : (isBlack ? BlackNormal : WhiteNormal);
        }

        e.Handled = true;
    }

    void ReleaseNote()
    {
        if (_pressedNote < 0) return;
        int ch = SelectedChannel - 1;
        _ = _sysEx.SendMidiAsync($"{0x80 | ch:X2} {_pressedNote:X2} 00");
        int released = _pressedNote;
        _pressedNote = -1;

        if (_pressedKey != null)
        {
            var (_, isBlack) = ((int, bool))_pressedKey.Tag!;
            if (!_midiLitNotes.Contains(released))
                _pressedKey.Background = isBlack ? BlackNormal : WhiteNormal;
            _pressedKey = null;
        }
    }

    // ── Pitch joystick ───────────────────────────────────────────────────────

    void BuildJoystick()
    {
        JoystickTrack.Height = WH;
        Canvas.SetTop(JoystickCenterLine, (WH - JoystickCenterLine.Height) / 2);
        PositionJoystickPuck();
    }

    void PositionJoystickPuck()
    {
        double usable = Math.Max(1, WH - JoystickPuck.Height);
        double top = (1 - (_jsValue + 1) / 2) * usable;
        Canvas.SetTop(JoystickPuck, top);
    }

    void OnJoyMouseDown(object sender, MouseButtonEventArgs e)
    {
        JoystickTrack.CaptureMouse();
        _jsDragging = true;
        UpdateJoystickFromMouse(e.GetPosition(JoystickTrack));
        e.Handled = true;
    }

    void OnJoyMouseMove(object sender, MouseEventArgs e)
    {
        if (!_jsDragging) return;
        UpdateJoystickFromMouse(e.GetPosition(JoystickTrack));
    }

    void OnJoyMouseUp(object sender, MouseButtonEventArgs e)
    {
        JoystickTrack.ReleaseMouseCapture();
        _jsDragging = false;   // OnJoystickTick springs it back to center from here
        e.Handled = true;
    }

    void UpdateJoystickFromMouse(Point p)
    {
        double usable = Math.Max(1, WH - JoystickPuck.Height);
        double frac = Math.Clamp((p.Y - JoystickPuck.Height / 2) / usable, 0, 1);
        _jsValue = 1 - 2 * frac;   // up = +1 (pitch up)
        PositionJoystickPuck();
        UpdateJoyLabel();
    }

    void OnJoystickRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var miUp = new MenuItem
        {
            Header = _jsUpKey.HasValue ? $"Reassign Bend-Up Key (currently {_jsUpKey})…" : "Assign Bend-Up Key…"
        };
        miUp.Click += (_, _) => BeginKeyCapture(
            k => _jsUpKey = k,
            onBegin: () => JoystickTrack.BorderBrush = StyleFilter.Border,
            onEnd:   () => JoystickTrack.BorderBrush = KeyBorder);
        menu.Items.Add(miUp);
        if (_jsUpKey.HasValue)
        {
            var miClearUp = new MenuItem { Header = $"Clear Bend-Up Key ({_jsUpKey})" };
            miClearUp.Click += (_, _) => { _jsUpKey = null; _jsUpHeld = false; };
            menu.Items.Add(miClearUp);
        }

        menu.Items.Add(new Separator());

        var miDown = new MenuItem
        {
            Header = _jsDownKey.HasValue ? $"Reassign Bend-Down Key (currently {_jsDownKey})…" : "Assign Bend-Down Key…"
        };
        miDown.Click += (_, _) => BeginKeyCapture(
            k => _jsDownKey = k,
            onBegin: () => JoystickTrack.BorderBrush = StyleFilter.Border,
            onEnd:   () => JoystickTrack.BorderBrush = KeyBorder);
        menu.Items.Add(miDown);
        if (_jsDownKey.HasValue)
        {
            var miClearDown = new MenuItem { Header = $"Clear Bend-Down Key ({_jsDownKey})" };
            miClearDown.Click += (_, _) => { _jsDownKey = null; _jsDownHeld = false; };
            menu.Items.Add(miClearDown);
        }

        menu.PlacementTarget = JoystickTrack;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // Ticks at 60fps. Mouse drag sets _jsValue directly (handled in
    // UpdateJoystickFromMouse); everything else - key-held motion and the spring-back
    // to center after a key/mouse release - glides here so it never snaps.
    void OnJoystickTick(object? sender, EventArgs e)
    {
        if (_jsDragging) return;

        double target = _jsUpHeld && !_jsDownHeld ? 1.0
                       : _jsDownHeld && !_jsUpHeld ? -1.0
                       : 0.0;
        if (Math.Abs(_jsValue - target) < 0.0005)
        {
            if (_jsValue == target) return;
            _jsValue = target;
        }
        else
        {
            _jsValue += (target - _jsValue) * 0.18;
        }

        PositionJoystickPuck();
        UpdateJoyLabel();
    }

    void UpdateJoyLabel()
    {
        TXT_JoyLabel.Text = Math.Abs(_jsValue) < 0.005 ? "0" : (_jsValue > 0 ? $"+{_jsValue:0.00}" : $"{_jsValue:0.00}");
    }

    // Runs at 15 Hz (see _jsSendTimer) - samples whatever _jsValue currently is and
    // sends only if it actually moved. Independent of how often the value itself
    // changes (60fps glide, or every mouse-move while dragging).
    void FlushJoystickSend()
    {
        int bend14 = Math.Clamp(8192 + (int)Math.Round(_jsValue * 8191), 0, 16383);
        if (bend14 == _jsLastSentBend) return;
        _jsLastSentBend = bend14;

        byte lsb = (byte)(bend14 & 0x7F);
        byte msb = (byte)((bend14 >> 7) & 0x7F);
        int ch = SelectedChannel - 1;
        _ = _sysEx.SendMidiAsync($"{0xE0 | ch:X2} {lsb:X2} {msb:X2}");
    }

    static string NoteName(int midi)
    {
        ReadOnlySpan<string> names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[midi % 12]}{midi / 12 - 1}";
    }

    // Called on a background thread (the MIDI consumer thread, or a TX caller).
    // Build the display row here - off the UI thread - and hand only the finished
    // object to the batch queue; the flush timer surfaces it. No per-message
    // Dispatcher call, so a memory-speed burst can't swamp the UI thread.
    void OnTraffic(SysExTrafficEntry entry)
    {
        _incoming.Enqueue((new SysExMessageItem(entry), entry.RawBytes, entry.IsMidi, entry.IsSend));
    }

    // Drains queued traffic onto the UI thread in one batch per tick: at most one
    // count refresh and one scroll regardless of how many messages arrived.
    void FlushIncoming(object? sender, EventArgs e)
    {
        if (_incoming.IsEmpty) return;

        int added = 0;
        const int maxPerTick = 4000;   // safety cap so a single tick can't itself stall
        while (added < maxPerTick && _incoming.TryDequeue(out var q))
        {
            added++;
            while (_allItems.Count >= MaxEntries)
                _allItems.RemoveAt(0);
            _allItems.Add(q.Item);

            // Light / un-light piano keys on incoming NoteOn / NoteOff from Kronos.
            if (q.IsMidi && !q.IsSend && q.Raw is { Length: >= 2 } raw)
                ApplyNoteLighting(raw);
        }

        if (added == 0) return;
        UpdateCount();
        if (CHK_AutoScroll.IsChecked == true)
            ScrollToBottom();
    }

    // UI-thread piano-key lighting for an incoming channel message on the selected
    // channel. Non-channel messages (SysEx, etc.) fall through harmlessly.
    void ApplyNoteLighting(byte[] raw)
    {
        byte status = raw[0];
        int inCh = (status & 0x0F) + 1;
        if (inCh != SelectedChannel) return;

        int type = status & 0xF0;
        int note = raw[1];
        bool isNoteOn  = type == 0x90 && raw.Length >= 3 && raw[2] > 0;
        bool isNoteOff = type == 0x80 || (type == 0x90 && raw.Length >= 3 && raw[2] == 0);

        if (isNoteOn && _keyByMidi.TryGetValue(note, out var keyOn))
        {
            _midiLitNotes.Add(note);
            if (note != _pressedNote)
            {
                var (_, isBlack) = ((int, bool))keyOn.Tag!;
                keyOn.Background = isBlack ? BlackPressed : WhitePressed;
            }
        }
        else if (isNoteOff && _keyByMidi.TryGetValue(note, out var keyOff))
        {
            _midiLitNotes.Remove(note);
            if (note != _pressedNote)
            {
                var (_, isBlack) = ((int, bool))keyOff.Tag!;
                keyOff.Background = isBlack ? BlackNormal : WhiteNormal;
            }
        }
    }

    void Clear()
    {
        _allItems.Clear();
        UpdateCount();
    }

    void CopySelected()
    {
        var items = LB_All.SelectedItems.Cast<SysExMessageItem>().ToList();
        if (items.Count == 0 && LB_All.SelectedItem is SysExMessageItem one) items.Add(one);
        CopyToClipboard(items);
    }

    void CopyAllShown() => CopyToClipboard(_view.Cast<SysExMessageItem>().ToList());

    static void CopyToClipboard(IReadOnlyList<SysExMessageItem> items)
    {
        if (items.Count == 0) return;
        var text = string.Join(Environment.NewLine, items.Select(i => i.CopyText));
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    void UpdateCount()
    {
        int sysExCount = _allItems.Count(i => i.MsgType == MidiMsgType.SysEx);
        TXT_SysExCount.Text = $"SysEx: {sysExCount}";
        TXT_MidiCount.Text  = $"MIDI: {_allItems.Count - sysExCount}";
    }

    void ScrollToBottom()
    {
        if (_allItems.Count > 0)
            LB_All.ScrollIntoView(_allItems[^1]);
    }
}

class SysExMessageItem
{
    static readonly SolidColorBrush ColorNote      = Frozen(0x88, 0xBB, 0xFF); // blue-ish
    static readonly SolidColorBrush ColorCC        = Frozen(0xFF, 0xCC, 0x66); // amber
    static readonly SolidColorBrush ColorProg      = Frozen(0xCC, 0x88, 0xFF); // purple
    static readonly SolidColorBrush ColorSysEx     = Frozen(0x77, 0xDD, 0x99); // green
    static readonly SolidColorBrush ColorBend      = Frozen(0xFF, 0x99, 0x66); // orange
    static readonly SolidColorBrush ColorAfterTouch= Frozen(0xFF, 0x77, 0xAA); // pink
    static readonly SolidColorBrush ColorTransport = Frozen(0xAA, 0xDD, 0xFF); // light blue
    static readonly SolidColorBrush ColorOther     = Frozen(0xCC, 0xCC, 0xCC); // default

    public string      Time      { get; }
    public string      Dir       { get; }
    public bool        IsSend    { get; }
    public string      Hex       { get; }
    public MidiMsgType MsgType   { get; }
    public Brush       TypeColor { get; }

    readonly byte[]? _raw;
    readonly string  _fallbackHex;

    public SysExMessageItem(SysExTrafficEntry entry)
    {
        Time   = entry.Timestamp.ToString("HH:mm:ss.fff");
        Dir    = entry.IsSend ? "TX" : "RX";
        IsSend = entry.IsSend;

        _raw         = entry.RawBytes is { Length: > 0 } ? entry.RawBytes : null;
        _fallbackHex = entry.Hex;

        // Decode the human-readable description HERE, on the enqueuing background
        // thread, rather than eagerly on the MIDI read/consumer thread for every
        // firehose message. Live-stream entries arrive with an empty Hex and only
        // RawBytes set; producing the hex for every one - with this window closed -
        // was the GC source while navigating. The embedded hex is capped (96 bytes)
        // so a bulk object (a Set List is ~79 KB) can neither build a ~½ MB string
        // nor hand the UI a 200k-char wrapping TextBlock to lay out. Full bytes stay
        // available for copy via RawHex.
        Hex = _raw != null
            ? MidiStreamMonitor.DecodeMidi(_raw, maxHexBytes: 96)
            : entry.Hex;

        var type  = Classify(entry.IsMidi, Hex);
        MsgType   = type;
        TypeColor = TypeToBrush(type);
    }

    // Full raw hex - built only when the row is actually copied, not per message,
    // and never shown in the (capped) live log where it would be unreadable and slow.
    public string RawHex => _raw != null
        ? string.Join(' ', _raw.Select(x => x.ToString("X2")))
        : _fallbackHex;

    // One clipboard line: timestamp, direction, raw hex.
    public string CopyText => $"{Time} {Dir} {RawHex}";

    static MidiMsgType Classify(bool isMidi, string h)
    {
        if (!isMidi) return MidiMsgType.SysEx;
        if (h.StartsWith("NoteOn",    StringComparison.Ordinal) ||
            h.StartsWith("NoteOff",   StringComparison.Ordinal)) return MidiMsgType.Note;
        if (h.StartsWith("CC#",       StringComparison.Ordinal)) return MidiMsgType.CC;
        if (h.StartsWith("PC",        StringComparison.Ordinal)) return MidiMsgType.ProgramChange;
        if (h.StartsWith("Bend",      StringComparison.Ordinal)) return MidiMsgType.PitchBend;
        if (h.StartsWith("ChPres",    StringComparison.Ordinal) ||
            h.StartsWith("PolyPres",  StringComparison.Ordinal)) return MidiMsgType.AfterTouch;
        if (h.StartsWith("SysEx",     StringComparison.Ordinal)) return MidiMsgType.SysEx;
        if (h.StartsWith("Start",     StringComparison.Ordinal) ||
            h.StartsWith("Stop",      StringComparison.Ordinal) ||
            h.StartsWith("Continue",  StringComparison.Ordinal) ||
            h.StartsWith("Reset",     StringComparison.Ordinal)) return MidiMsgType.Transport;
        return MidiMsgType.Other;
    }

    static Brush TypeToBrush(MidiMsgType t) => t switch
    {
        MidiMsgType.Note          => ColorNote,
        MidiMsgType.CC            => ColorCC,
        MidiMsgType.ProgramChange => ColorProg,
        MidiMsgType.SysEx         => ColorSysEx,
        MidiMsgType.PitchBend     => ColorBend,
        MidiMsgType.AfterTouch    => ColorAfterTouch,
        MidiMsgType.Transport     => ColorTransport,
        _                         => ColorOther,
    };

}
