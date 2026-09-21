namespace KronosScreenRemote;

// Purely a XAML container - see KronosLeftPanel.xaml.cs's header comment; the same rationale
// applies here. All wiring/state (mode-button lighting, data-entry clicks, the data wheel,
// the Focused-preset rail) stays in MainWindow.xaml.cs/.Input.cs/.Streaming.cs, qualified
// through MainWindow's _kronosRightPanel field instead of bare field access.
public partial class KronosRightPanel
{
    public KronosRightPanel()
    {
        InitializeComponent();
    }
}
