using System.Windows;
using System.Windows.Media;

namespace KronosScreenRemote;

// Purely a XAML container - see KronosLeftPanel.xaml.cs's header comment. The buttons on it are
// wired by MainWindow (WireButtons; MODE/PAGE lighting by ApplyNautilusLamps).
public partial class NautilusRightPanel
{
    const double DesignWidth = 1024;

    public NautilusRightPanel()
    {
        InitializeComponent();
    }

    // Scale the 1024x771 design canvas by WIDTH ONLY (never by whichever of width/height is
    // more constraining, unlike a Viewbox's Uniform/UniformToFill) so the panel always matches
    // the live screen's column width exactly and only ever crops top/bottom (generous ~150-unit
    // margin above/below the buttons) rather than the tight left/right margins around
    // EXIT/PAGE. If the window is narrowed enough that this scale no longer covers the full
    // column height, the panel just falls short of the screen's height (a gap, not a crop) -
    // the live screen keeps its own presentation either way, so that's the safe trade-off.
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double scale = e.NewSize.Width / DesignWidth;
        DesignGrid.LayoutTransform = new ScaleTransform(scale, scale);
    }
}
