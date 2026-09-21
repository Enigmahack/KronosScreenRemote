using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KronosScreenRemote;

// Drop-in PasswordBox replacement with a small eye toggle to reveal/mask the value - for
// credential fields where the user occasionally needs to visually confirm what they typed
// (Settings' FTP password, LoginDialog's password prompt on first connect / after a failed
// login). Exposes the same Password/Focus/Clear surface PasswordBox itself does, so call
// sites only need the XAML element type changed, not their code-behind.
//
// Internally a PasswordBox and a plain TextBox occupy the same cell, only one visible at a
// time; PART_Password is the only one masking input, so it's the one actually holding the
// "real" value whenever the reveal isn't showing. Both are kept in sync on every edit via
// the _syncing reentrancy guard, so switching the view mid-edit never loses a keystroke.
public partial class RevealablePasswordBox : UserControl
{
    static readonly Brush EyeDim    = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    static readonly Brush EyeActive = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

    bool _revealed;
    bool _syncing;

    public RevealablePasswordBox()
    {
        InitializeComponent();
    }

    public string Password
    {
        get => PART_Password.Password;
        set
        {
            _syncing = true;
            PART_Password.Password = value;
            PART_Reveal.Text = value ?? "";
            _syncing = false;
        }
    }

    // Hides (not overrides - UIElement.Focus() isn't virtual) UIElement.Focus() so callers
    // that already do PwdBox.Focus() keep working, focusing whichever of the two is visible.
    public new bool Focus() => (_revealed ? (UIElement)PART_Reveal : PART_Password).Focus();

    public void Clear()
    {
        PART_Password.Clear();
        PART_Reveal.Clear();
    }

    void Password_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        PART_Reveal.Text = PART_Password.Password;
        _syncing = false;
    }

    void Reveal_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        PART_Password.Password = PART_Reveal.Text;
        _syncing = false;
    }

    void Eye_Click(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        PART_Password.Visibility = _revealed ? Visibility.Collapsed : Visibility.Visible;
        PART_Reveal.Visibility   = _revealed ? Visibility.Visible   : Visibility.Collapsed;
        EyeGlyph.Foreground = _revealed ? EyeActive : EyeDim;
        PART_Eye.ToolTip     = _revealed ? "Hide password" : "Show password";
        Focus();
    }
}
