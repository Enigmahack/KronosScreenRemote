namespace KronosScreenRemote;

// Purely a XAML container - all wiring/state (value-slider drag, Inc/Dec commands, the
// Focused-preset rail) stays in MainWindow.xaml.cs/.Input.cs exactly as before this was split
// out, just qualified through MainWindow's _kronosLeftPanel field instead of bare field access.
// See KronosLeftPanel.xaml's own header comment for why this split exists.
public partial class KronosLeftPanel
{
    public KronosLeftPanel()
    {
        InitializeComponent();
    }
}
