using System.Windows;

namespace KronosScreenRemote;

/// <summary>
/// Shared base for every app window. Applies the dark-theme brushes and the dark title-bar
/// caption from one place — collapsing the per-window <c>Background</c>/<c>Foreground</c> root
/// re-declaration and the <c>WindowTheme.ApplyDarkCaption(this)</c> call that used to be copied
/// into every constructor — and enforces consistent window behavior:
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
    /// Owned tool/dialog windows keep the default (<c>false</c>) — no minimize box. Only the
    /// un-owned root window (<c>MainWindow</c>) overrides this to <c>true</c>; it legitimately
    /// minimizes to the system tray. Read from the base ctor, so overrides must be constant
    /// expression-bodied properties (no instance-field access).
    /// </summary>
    protected virtual bool AllowMinimize => false;

    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        WindowTheme.ApplyDarkCaption(this);

        if (!AllowMinimize)
            WindowTheme.DisableMinimizeBox(this);
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
