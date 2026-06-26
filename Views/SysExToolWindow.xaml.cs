using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

enum FilterState { On, Filter, Off }

enum MidiMsgType { Note, CC, ProgramChange, PitchBend, AfterTouch, SysEx, Transport, Other }

partial class SysExToolWindow : Window
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

    readonly ISysExService _sysEx;
    readonly ObservableCollection<SysExMessageItem> _allItems = new();
    ICollectionView _view = null!;

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

    public int SelectedChannel => CMB_OutChannel.SelectedIndex >= 0 ? CMB_OutChannel.SelectedIndex + 1 : 1;

    public SysExToolWindow(ISysExService sysEx, int initialChannel = 1)
    {
        _sysEx = sysEx;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        _view = CollectionViewSource.GetDefaultView(_allItems);
        _view.Filter = FilterMessage;
        LB_All.ItemsSource = _view;

        BTN_Clear.Click += (_, _) => Clear();

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
        Closed += (_, _) =>
        {
            _sysEx.SysExTraffic -= OnTraffic;
            ReleaseNote();
            ClearMidiLitKeys();
        };

        Loaded += (_, _) => BuildPiano();
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

    // ── Filter buttons ───────────────────────────────────────────────────────

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

    // ── Piano ────────────────────────────────────────────────────────────────

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
        var (midi, isBlack) = ((int, bool))key.Tag!;

        ReleaseNote();

        _pressedKey  = key;
        _pressedNote = midi;
        key.Background = isBlack ? BlackPressed : WhitePressed;
        key.CaptureMouse();

        int ch = SelectedChannel - 1;
        _ = _sysEx.SendMidiAsync($"{0x90 | ch:X2} {midi:X2} 64");
        TXT_NoteLabel.Text = NoteName(midi);
        e.Handled = true;
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

    static string NoteName(int midi)
    {
        ReadOnlySpan<string> names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[midi % 12]}{midi / 12 - 1}";
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ── Traffic routing ──────────────────────────────────────────────────────

    void OnTraffic(SysExTrafficEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            while (_allItems.Count >= MaxEntries)
                _allItems.RemoveAt(0);

            _allItems.Add(new SysExMessageItem(entry));
            UpdateCount();

            if (CHK_AutoScroll.IsChecked == true)
                ScrollToBottom();

            // Light / un-light piano keys on incoming NoteOn / NoteOff from Kronos.
            if (entry.IsMidi && !entry.IsSend && entry.RawBytes is { Length: >= 2 } raw)
            {
                byte status = raw[0];
                int inCh = (status & 0x0F) + 1;
                if (inCh == SelectedChannel)
                {
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
            }
        });
    }

    void Clear()
    {
        _allItems.Clear();
        UpdateCount();
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

class SysExMessageItem(SysExTrafficEntry entry)
{
    static readonly SolidColorBrush ColorNote      = MakeBrush(0x88, 0xBB, 0xFF); // blue-ish
    static readonly SolidColorBrush ColorCC        = MakeBrush(0xFF, 0xCC, 0x66); // amber
    static readonly SolidColorBrush ColorProg      = MakeBrush(0xCC, 0x88, 0xFF); // purple
    static readonly SolidColorBrush ColorSysEx     = MakeBrush(0x77, 0xDD, 0x99); // green
    static readonly SolidColorBrush ColorBend      = MakeBrush(0xFF, 0x99, 0x66); // orange
    static readonly SolidColorBrush ColorAfterTouch= MakeBrush(0xFF, 0x77, 0xAA); // pink
    static readonly SolidColorBrush ColorTransport = MakeBrush(0xAA, 0xDD, 0xFF); // light blue
    static readonly SolidColorBrush ColorOther     = MakeBrush(0xCC, 0xCC, 0xCC); // default

    public string      Time      { get; } = entry.Timestamp.ToString("HH:mm:ss.fff");
    public string      Dir       { get; } = entry.IsSend ? "TX" : "RX";
    public bool        IsSend    { get; } = entry.IsSend;
    public string      Hex       { get; } = entry.Hex;
    public MidiMsgType MsgType   { get; } = Classify(entry);
    public Brush       TypeColor { get; } = TypeToBrush(Classify(entry));

    static MidiMsgType Classify(SysExTrafficEntry e)
    {
        if (!e.IsMidi) return MidiMsgType.SysEx;
        var h = e.Hex;
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

    static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
