using System.Windows;

namespace KronosScreenRemote;

/// <summary>
/// Shared base for every app window. Applies the dark-theme brushes and the dark title-bar
/// caption from one place - collapsing the per-window <c>Background</c>/<c>Foreground</c> root
/// re-declaration and the <c>WindowTheme.ApplyDarkCaption(this)</c> call that used to be copied
/// into every constructor - and enforces consistent window behavior:
/// <list type="bullet">
///   <item>spawns centered on its owner (<see cref="Window.WindowStartupLocation"/> = CenterOwner);</item>
///   <item>cannot be minimized unless it opts back in via <see cref="AllowMinimize"/>. An owned,
///         non-taskbar window that minimizes otherwise strands itself as a title-bar stub in a
///         screen corner (the Librarian "disappears to the bottom-left" bug).</item>
/// </list>
/// Brushes are set via resource reference (not literals) so <c>Themes/Dark.xaml</c> stays the
/// single source of truth; because the ctor runs before <c>InitializeComponent()</c>, a derived
/// window's XAML root no longer needs <c>Background</c>/<c>Foreground</c> at all.
/// </summary>
public class ThemedWindow : Window
{
    /// <summary>
    /// Owned tool/dialog windows keep the default (<c>false</c>) - no minimize box. Only the
    /// un-owned root window (<c>MainWindow</c>) overrides this to <c>true</c>; it legitimately
    /// minimizes to the system tray. Read from the base ctor, so overrides must be constant
    /// expression-bodied properties (no instance-field access).
    /// </summary>
    protected virtual bool AllowMinimize => false;

    /// <summary>
    /// Whether this window reopens where it was last closed (<see cref="AppSettings.WindowPlacements"/>,
    /// keyed by type name). Defaults to "yes, if the window is resizable" - a fixed-size confirm or
    /// prompt dialog stays centered on its owner, which is where a small modal belongs; a real tool
    /// window (Librarian, Sample Editor, File Manager, ...) is one the user arranges and expects to
    /// find again. Evaluated at <see cref="Window.SourceInitialized"/>, not in the ctor, because
    /// <see cref="Window.ResizeMode"/> comes from the derived window's XAML, which is parsed after
    /// the base ctor has already run.
    /// <para><c>MainWindow</c> overrides this to <c>false</c>: it has its own dedicated geometry
    /// fields, tangled up with the tray and fullscreen paths.</para>
    /// </summary>
    protected virtual bool RemembersPlacement =>
        ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        WindowTheme.ApplyDarkCaption(this);

        if (!AllowMinimize)
            WindowTheme.DisableMinimizeBox(this);

        SourceInitialized += (_, _) => RestorePlacement();
        Closing += (_, _) => SavePlacement();
    }

    string PlacementKey => GetType().Name;

    void RestorePlacement()
    {
        if (!RemembersPlacement) return;
        try
        {
            if (Storage.LoadSettings().WindowPlacements.GetValueOrDefault(PlacementKey) is not { } p) return;
            if (p.Width < 200 || p.Height < 100) return;   // never restore a degenerate size

            // Clamped onto the CURRENT virtual desktop, leaving a strip of title bar reachable: a
            // window last closed on a monitor that is no longer attached would otherwise reopen
            // entirely off-screen, with no way to drag it back.
            double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120;
            double maxTop  = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;
            Left = Math.Min(Math.Max(p.Left, SystemParameters.VirtualScreenLeft), maxLeft);
            Top  = Math.Min(Math.Max(p.Top,  SystemParameters.VirtualScreenTop),  maxTop);
            Width = p.Width;
            Height = p.Height;
            // Only AFTER a successful restore - otherwise a window with nothing saved yet would
            // lose CenterOwner and open at whatever the XAML happened to declare.
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (p.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            // Geometry is a convenience; a corrupt or unreadable settings file must never stop a
            // window from opening.
            AppLog.Warn($"[window] restore placement for {PlacementKey} failed: {ex.Message}");
        }
    }

    void SavePlacement()
    {
        if (!RemembersPlacement) return;
        // RestoreBounds, not Left/Top/Width/Height: it reports the NORMAL-state rectangle even
        // while maximized, so un-maximizing after a restart returns to the user's own size. It is
        // also Rect.Empty for a window that was never shown, which is what keeps the headless UI
        // smoke test (which constructs every window and never shows one) from writing to the real
        // settings.json beside the exe.
        var r = RestoreBounds;
        if (r.Width < 200 || r.Height < 100) return;
        try
        {
            // Read-modify-write against what's on disk right now, rather than against a cached
            // AppSettings: several windows own their own copy of it, and clobbering the whole file
            // with a stale one to save a rectangle would lose whatever another window just changed.
            var settings = Storage.LoadSettings();
            settings.WindowPlacements[PlacementKey] = new WindowPlacement
            {
                Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
                Maximized = WindowState == WindowState.Maximized,
            };
            Storage.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[window] save placement for {PlacementKey} failed: {ex.Message}");
        }
    }
}

/// <summary>Fluent helpers for the repeated owner-setup idiom at the window call sites.</summary>
public static class WindowExtensions
{
    /// <summary>
    /// Sets <see cref="Window.Owner"/> and returns the window, so a caller can write
    /// <c>new SomeWindow(...).OwnedBy(this).ShowDialog()</c> (modal) or <c>.OwnedBy(this).Show()</c>
    /// (modeless) instead of the object-initializer <c>{ Owner = this }</c> form repeated across
    /// every open site. Combined with the CenterOwner default above, this centralizes how a child
    /// window attaches to its owner.
    /// </summary>
    public static T OwnedBy<T>(this T window, Window owner) where T : Window
    {
        window.Owner = owner;
        return window;
    }
}
