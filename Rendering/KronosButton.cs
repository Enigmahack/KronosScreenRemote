using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KronosScreenRemote;

public enum KButtonMode
{
    None,       // No visual change on click (Exit, Enter, number pad)
    Momentary,  // Briefly shows Lit state then reverts
    Toggle,     // Flips IsActive on each click
    Radio       // Stays Lit; deactivates all others in the same RadioGroup
}

/// <summary>
/// Image-based button that switches between UnlitSource and LitSource on click.
/// Four modes: None (no animation), Momentary (flash), Toggle (latch), Radio (exclusive latch).
/// </summary>
public class KronosButton : Button
{
    // ── Radio group registry (weak refs so GC can collect removed buttons) ────
    static readonly Dictionary<string, List<WeakReference<KronosButton>>> _groups = new();

    // ── Dependency Properties ─────────────────────────────────────────────────

    // String paths — avoid ImageSourceConverter at XAML parse time so missing
    // images degrade gracefully instead of throwing XamlParseException.
    public static readonly DependencyProperty UnlitSourceProperty =
        DependencyProperty.Register(nameof(UnlitSource), typeof(string), typeof(KronosButton),
            new PropertyMetadata(null, (d, _) => ((KronosButton)d).SyncDisplay()));

    public static readonly DependencyProperty LitSourceProperty =
        DependencyProperty.Register(nameof(LitSource), typeof(string), typeof(KronosButton),
            new PropertyMetadata(null, (d, _) => ((KronosButton)d).SyncDisplay()));

    public static readonly DependencyProperty ButtonModeProperty =
        DependencyProperty.Register(nameof(ButtonMode), typeof(KButtonMode), typeof(KronosButton),
            new PropertyMetadata(KButtonMode.None));

    public static readonly DependencyProperty RadioGroupProperty =
        DependencyProperty.Register(nameof(RadioGroup), typeof(string), typeof(KronosButton),
            new PropertyMetadata(null, OnRadioGroupChanged));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(KronosButton),
            new PropertyMetadata(false, (d, _) => ((KronosButton)d).SyncDisplay()));

    // Internal — the template Image binds to this
    static readonly DependencyProperty DisplaySourceProperty =
        DependencyProperty.Register("DisplaySource", typeof(ImageSource), typeof(KronosButton));

    // ── CLR wrappers ──────────────────────────────────────────────────────────

    public string?      UnlitSource { get => (string?)GetValue(UnlitSourceProperty); set => SetValue(UnlitSourceProperty, value); }
    public string?      LitSource   { get => (string?)GetValue(LitSourceProperty);   set => SetValue(LitSourceProperty,   value); }
    public KButtonMode  ButtonMode  { get => (KButtonMode) GetValue(ButtonModeProperty);  set => SetValue(ButtonModeProperty,  value); }
    public string?      RadioGroup  { get => (string?)     GetValue(RadioGroupProperty);  set => SetValue(RadioGroupProperty,  value); }
    public bool         IsActive    { get => (bool)        GetValue(IsActiveProperty);    set => SetValue(IsActiveProperty,    value); }

    // ── Flash timer (Momentary mode) ──────────────────────────────────────────

    readonly DispatcherTimer _flash;
    const int FlashMs = 150;

    // ── Depress transform (None-mode buttons only) ────────────────────────────

    readonly TranslateTransform _depress = new();
    readonly DispatcherTimer _depressTimer;
    const int DepressMs = 150;

    // ── Constructor ───────────────────────────────────────────────────────────

    public KronosButton()
    {
        // Suppress button chrome
        Padding          = new Thickness(0);
        BorderThickness  = new Thickness(0);
        Background       = Brushes.Transparent; // Transparent = hit-testable
        FocusVisualStyle = null;
        Focusable        = false;  // keep keyboard focus on the main window
        IsTabStop        = false;
        RenderTransform  = _depress;

        _flash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FlashMs) };
        _flash.Tick += (_, _) => { _flash.Stop(); IsActive = false; };

        _depressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DepressMs) };
        _depressTimer.Tick += (_, _) => { _depressTimer.Stop(); _depress.Y = 0; };

        // Template: a single Image, source driven by DisplaySource DP
        var imgFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        imgFactory.SetValue(System.Windows.Controls.Image.StretchProperty, Stretch.Fill);
        imgFactory.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        imgFactory.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);
        imgFactory.SetBinding(System.Windows.Controls.Image.SourceProperty,
            new Binding("DisplaySource") { RelativeSource = RelativeSource.TemplatedParent });

        Template = new ControlTemplate(typeof(KronosButton)) { VisualTree = imgFactory };
    }

    // ── Radio group registration ──────────────────────────────────────────────

    static void OnRadioGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var btn = (KronosButton)d;

        if (e.OldValue is string oldGroup && _groups.TryGetValue(oldGroup, out var old))
            old.RemoveAll(r => !r.TryGetTarget(out var t) || t == btn);

        if (e.NewValue is string newGroup)
        {
            if (!_groups.TryGetValue(newGroup, out var list))
                _groups[newGroup] = list = new();
            list.Add(new WeakReference<KronosButton>(btn));
        }
    }

    // ── Image cache (app-wide; avoids re-decoding the same pack URI repeatedly) ─

    static readonly Dictionary<string, ImageSource?> _imgCache = new();

    static ImageSource? LoadImage(string? src)
    {
        if (string.IsNullOrEmpty(src)) return null;
        if (_imgCache.TryGetValue(src, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            // Relative paths like "/Resources/Images/Foo.png" are pack URIs
            // embedded in the assembly. In code-behind there is no implicit
            // base URI, so we must build the absolute pack URI explicitly.
            var uri = src.StartsWith('/')
                ? new Uri("pack://application:,,," + src, UriKind.Absolute)
                : new Uri(src, UriKind.RelativeOrAbsolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource   = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            img = bmp;
        }
        catch { }
        _imgCache[src] = img;
        return img;
    }

    // ── Display sync ──────────────────────────────────────────────────────────

    void SyncDisplay()
        => SetValue(DisplaySourceProperty, LoadImage(IsActive ? LitSource : UnlitSource));

    // ── Click ─────────────────────────────────────────────────────────────────

    protected override void OnClick()
    {
        base.OnClick(); // fires the public Click event for code-behind handlers

        switch (ButtonMode)
        {
            case KButtonMode.Momentary:
                _flash.Stop();
                IsActive = true;
                _flash.Start();
                break;

            case KButtonMode.Toggle:
                IsActive = !IsActive;
                break;

            case KButtonMode.Radio:
                if (IsActive) return; // already lit — no-op
                IsActive = true;
                DeactivateGroupPeers();
                break;

            case KButtonMode.None:
                break;
        }
    }

    void DeactivateGroupPeers()
    {
        if (RadioGroup is not { } grp || !_groups.TryGetValue(grp, out var list)) return;
        foreach (var wref in list)
            if (wref.TryGetTarget(out var other) && other != this)
                other.IsActive = false;
    }

    // ── Depress feedback (None-mode buttons only) ─────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ButtonMode == KButtonMode.None) _depress.Y = 2;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _depress.Y = 0;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _depress.Y = 0;
    }

    // Guarantee the full layout rect is always hit-testable, even when the
    // image source is null (no content = no automatic hit surface otherwise).
    protected override HitTestResult HitTestCore(PointHitTestParameters p)
        => new PointHitTestResult(this, p.HitPoint);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Briefly indents the button visually (keyboard feedback for None-mode buttons).</summary>
    public void FlashDepress()
    {
        _depress.Y = 2;
        _depressTimer.Stop();
        _depressTimer.Start();
    }

    /// <summary>Programmatically activate this button and deactivate group peers.</summary>
    public void Activate()
    {
        IsActive = true;
        DeactivateGroupPeers();
    }
}
