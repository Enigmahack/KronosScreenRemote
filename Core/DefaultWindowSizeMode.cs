namespace KronosScreenRemote;

// Settings > View > "Default Window Size" - what MainWindow's geometry restore
// (MainWindow.Input.cs OnLoaded) applies at launch instead of the saved WindowWidth/Height.
public enum DefaultWindowSizeMode
{
    LastUsed,
    Small,
    Medium,
    Large,
    Maximized,
}
