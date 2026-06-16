using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class ControlPaletteWindow : Window
{
    readonly Action<string> _ctrl;

    // Wheel drag state
    bool   _wheelDragActive = false;
    double _wheelDragStartY = 0;
    int    _wheelDragSteps  = 0;
    const double WheelPxPerStep = 12;

    readonly DispatcherTimer _wheelAnimTimer = new();
    int      _wheelAnimState = 0;
    int      _wheelAnimDir   = 1;
    DateTime _wheelLastActivity = DateTime.MinValue;
    const int WheelAnimIntervalMs = 100;
    const int WheelAnimIdleMs     = 400;
    static readonly double[] WheelAngles = { 0.0, 10.0, -10.0 };

    public ControlPaletteWindow(Action<string> ctrl, Action<int>? onUserModeChange = null)
    {
        _ctrl = ctrl;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        // Mode buttons — pass mode number so MainWindow can track pending confirmation
        BTN_Setlist.Click  += (_, _) => { onUserModeChange?.Invoke(1); ctrl("BUTTON SETLIST"); };
        BTN_Combi.Click    += (_, _) => { onUserModeChange?.Invoke(2); ctrl("BUTTON COMBI"); };
        BTN_Program.Click  += (_, _) => { onUserModeChange?.Invoke(3); ctrl("BUTTON PROGRAM"); };
        BTN_Sequence.Click += (_, _) => { onUserModeChange?.Invoke(4); ctrl("BUTTON SEQUENCE"); };
        BTN_Sampling.Click += (_, _) => { onUserModeChange?.Invoke(5); ctrl("BUTTON SAMPLING"); };
        BTN_Global.Click   += (_, _) => { onUserModeChange?.Invoke(6); ctrl("BUTTON GLOBAL"); };
        BTN_Disk.Click     += (_, _) => { onUserModeChange?.Invoke(7); ctrl("BUTTON DISK"); };

        // Toggle buttons
        BTN_Help.Click    += (_, _) => ctrl("BUTTON HELP");
        BTN_Compare.Click += (_, _) => ctrl("BUTTON COMPARE");

        // Number pad
        BTN_data_dash.Click   += (_, _) => ctrl("BUTTON NUM_DASH");
        BTN_data0.Click       += (_, _) => ctrl("BUTTON NUM0");
        BTN_data_period.Click += (_, _) => ctrl("BUTTON NUM_DOT");
        BTN_data1.Click       += (_, _) => ctrl("BUTTON NUM1");
        BTN_data2.Click       += (_, _) => ctrl("BUTTON NUM2");
        BTN_data3.Click       += (_, _) => ctrl("BUTTON NUM3");
        BTN_data4.Click       += (_, _) => ctrl("BUTTON NUM4");
        BTN_data5.Click       += (_, _) => ctrl("BUTTON NUM5");
        BTN_data6.Click       += (_, _) => ctrl("BUTTON NUM6");
        BTN_data7.Click       += (_, _) => ctrl("BUTTON NUM7");
        BTN_data8.Click       += (_, _) => ctrl("BUTTON NUM8");
        BTN_data9.Click       += (_, _) => ctrl("BUTTON NUM9");

        // Exit / Enter
        BTN_Exit.Click  += (_, _) => ctrl("BUTTON EXIT");
        BTN_Enter.Click += (_, _) => ctrl("BUTTON ENTER");

        // Wheel
        Data_Wheel.MouseDown        += OnWheelMouseDown;
        Data_Wheel.MouseMove        += OnWheelMouseMove;
        Data_Wheel.MouseUp          += OnWheelMouseUp;
        Data_Wheel.LostMouseCapture += (_, _) => _wheelDragActive = false;
        MouseWheel += (_, e) =>
        {
            bool cw = e.Delta > 0;
            ctrl(cw ? "WHEEL CW" : "WHEEL CCW");
            TriggerWheelAnim(cw ? 1 : -1);
        };

        _wheelAnimTimer.Interval = TimeSpan.FromMilliseconds(WheelAnimIntervalMs);
        _wheelAnimTimer.Tick    += (_, _) => AdvanceWheelAnim();
    }

    // ── Mode sync (called by MainWindow when OCR or poll updates the mode) ────

    public void SetMode(int mode)
    {
        var btn = mode switch
        {
            1 => BTN_Setlist,
            2 => BTN_Combi,
            3 => BTN_Program,
            4 => BTN_Sequence,
            5 => BTN_Sampling,
            6 => BTN_Global,
            7 => BTN_Disk,
            _ => (KronosButton?)null
        };
        btn?.Activate();
    }

    // ── Wheel drag / animation ────────────────────────────────────────────────

    void OnWheelMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _wheelDragActive = true;
        _wheelDragStartY = e.GetPosition(Data_Wheel).Y;
        _wheelDragSteps  = 0;
        Data_Wheel.CaptureMouse();
        e.Handled = true;
    }

    void OnWheelMouseMove(object s, MouseEventArgs e)
    {
        if (!_wheelDragActive) return;
        double dy    = _wheelDragStartY - e.GetPosition(Data_Wheel).Y;
        int    steps = (int)(dy / WheelPxPerStep);
        int    diff  = steps - _wheelDragSteps;
        if (diff > 0)
            for (int i = 0; i < diff;  i++) { _ctrl("WHEEL CW");  TriggerWheelAnim(1);  }
        else if (diff < 0)
            for (int i = 0; i < -diff; i++) { _ctrl("WHEEL CCW"); TriggerWheelAnim(-1); }
        if (diff != 0) _wheelDragSteps = steps;
        e.Handled = true;
    }

    void OnWheelMouseUp(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _wheelDragActive = false;
        Data_Wheel.ReleaseMouseCapture();
        e.Handled = true;
    }

    void TriggerWheelAnim(int dir)
    {
        _wheelAnimDir      = dir;
        _wheelLastActivity = DateTime.Now;
        if (!_wheelAnimTimer.IsEnabled)
        {
            _wheelAnimTimer.Start();
            AdvanceWheelAnim();
        }
    }

    void AdvanceWheelAnim()
    {
        if ((DateTime.Now - _wheelLastActivity).TotalMilliseconds > WheelAnimIdleMs)
        {
            _wheelAnimTimer.Stop();
            return;
        }
        _wheelAnimState = (_wheelAnimState + _wheelAnimDir + 3) % 3;
        WheelRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        WheelRotate.Angle = WheelAngles[_wheelAnimState];
    }
}
