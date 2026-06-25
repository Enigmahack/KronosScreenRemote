using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

partial class SysExToolWindow : Window
{
    const int MaxEntries = 1000;

    // White key stride in px; keys are this wide minus a 1 px gap.
    const double WW = 28;
    const double WH = 148; // white key height = canvas height
    const double BW = 16;  // black key width
    const double BH = 92;  // black key height

    static readonly Brush WhiteNormal  = Frozen(0xDC, 0xDC, 0xDC);
    static readonly Brush WhiteHover   = Frozen(0xF0, 0xF0, 0xF0);
    static readonly Brush WhitePressed = Frozen(0x90, 0xB8, 0xFF);
    static readonly Brush BlackNormal  = Frozen(0x1E, 0x1E, 0x1E);
    static readonly Brush BlackHover   = Frozen(0x32, 0x32, 0x32);
    static readonly Brush BlackPressed = Frozen(0x18, 0x38, 0x68);
    static readonly Brush KeyBorder    = Frozen(0x44, 0x44, 0x44);

    readonly ISysExService _sysEx;
    readonly ObservableCollection<SysExMessageItem> _items = new();

    Border? _pressedKey;
    int     _pressedNote = -1;

    public SysExToolWindow(ISysExService sysEx)
    {
        _sysEx = sysEx;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        LB_Messages.ItemsSource = _items;
        BTN_Clear.Click += (_, _) => Clear();

        _sysEx.SysExTraffic += OnTraffic;
        Closed += (_, _) =>
        {
            _sysEx.SysExTraffic -= OnTraffic;
            ReleaseNote();
        };

        Loaded += (_, _) => BuildPiano();
    }

    // ── Piano ────────────────────────────────────────────────────────────────

    void BuildPiano()
    {
        PianoCanvas.Height = WH;

        // Seven octaves: A0 (21) through A7 (93)
        const int startNote = 21;
        const int startOffset = 3; // 0 = C, 1 = B, 2 = Bb, 3 = A, etc. 

        // Semitone offsets of white keys within one octave (C=0)
        int[] whiteSemitones = [0, 2, 4, 5, 7, 9, 11];

        // For each black key: left-white-key index within octave + semitone
        (int leftWhite, int semitone)[] blackKeys =
            [(0, 1), (1, 3), (3, 6), (4, 8), (5, 10)];

        var whites = new List<(double x, int midi)>();
        var blacks = new List<(double x, int midi)>();

        // Add the A, Bb and B keys before doing the rest. 
        whites.Add((0, 21));
        blacks.Add((18, 22));
        whites.Add((28, 23));

        for (int oct = 0; oct < 7; oct++)
        {
            int wBase = oct * 7;
            int mBase = oct * 12;

            for (int i = 0; i < whiteSemitones.Length; i++)
                whites.Add(((wBase + i + 1 + (startOffset / 2)) * WW, startNote + startOffset + mBase + whiteSemitones[i]));

            foreach (var (lw, st) in blackKeys)
                blacks.Add(((wBase + lw + 2 + (startOffset / 2)) * WW - BW / 2.0, startNote + startOffset + mBase + st));
        }

        // Add the last C
        whites.Add((1428, 108));

        // White keys first so black keys render on top
        foreach (var (x, midi) in whites)
            AddKey(x, WW - 1, WH, false, midi);

        foreach (var (x, midi) in blacks)
            AddKey(x, BW, BH, true, midi);
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
    }

    void OnKeyEnter(object sender, MouseEventArgs e)
    {
        var key = (Border)sender;
        var (midi, isBlack) = ((int, bool))key.Tag!;
        if (key != _pressedKey)
            key.Background = isBlack ? BlackHover : WhiteHover;
        TXT_NoteLabel.Text = NoteName(midi);
    }

    void OnKeyLeave(object sender, MouseEventArgs e)
    {
        var key = (Border)sender;
        var (midi, isBlack) = ((int, bool))key.Tag!;
        if (key != _pressedKey)
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

        _ = _sysEx.SendMidiAsync($"90 {midi:X2} 64");
        TXT_NoteLabel.Text = NoteName(midi);
        e.Handled = true;
    }

    void OnKeyUp(object sender, MouseButtonEventArgs e)
    {
        var key = (Border)sender;
        key.ReleaseMouseCapture();

        ReleaseNote();

        var (_, isBlack) = ((int, bool))key.Tag!;
        key.Background = Mouse.DirectlyOver == key
            ? (isBlack ? BlackHover : WhiteHover)
            : (isBlack ? BlackNormal : WhiteNormal);

        e.Handled = true;
    }

    void ReleaseNote()
    {
        if (_pressedNote < 0) return;
        _ = _sysEx.SendMidiAsync($"80 {_pressedNote:X2} 00");
        _pressedNote = -1;

        if (_pressedKey != null)
        {
            var (_, isBlack) = ((int, bool))_pressedKey.Tag!;
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
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }

    // ── SysEx traffic ────────────────────────────────────────────────────────

    void OnTraffic(SysExTrafficEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            while (_items.Count >= MaxEntries)
                _items.RemoveAt(0);

            _items.Add(new SysExMessageItem(entry));
            UpdateCount();

            if (CHK_AutoScroll.IsChecked == true)
                ScrollToBottom();
        });
    }

    void Clear()
    {
        _items.Clear();
        UpdateCount();
    }

    void UpdateCount() => TXT_Count.Text = $"{_items.Count} message{(_items.Count == 1 ? "" : "s")}";

    void ScrollToBottom()
    {
        if (_items.Count == 0) return;
        LB_Messages.ScrollIntoView(_items[^1]);
    }
}

class SysExMessageItem(SysExTrafficEntry entry)
{
    public string Time   { get; } = entry.Timestamp.ToString("HH:mm:ss.fff");
    public string Dir    { get; } = entry.IsSend ? "TX" : "RX";
    public bool   IsSend { get; } = entry.IsSend;
    public string Hex    { get; } = entry.Hex;
}
