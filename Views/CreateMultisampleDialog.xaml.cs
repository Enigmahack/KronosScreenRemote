using System.Windows;

namespace KronosScreenRemote;

// "Create" button on the Multisample (MS) panel - asks only what the format itself
// forces a choice on (mono vs. stereo: a stereo instrument is a matched pair of two
// full multisamples, doc §2.2, not something you can add after the fact without
// rebuilding it as a pair). Name and slot number are NOT asked here - the slot is the
// next free one (computed by the caller, shown in the title so the user knows what
// they're about to get), and the multisample gets the same auto-generated name a real
// Kronos gives a fresh one ("NEWMS<slot>") - rename afterward via the existing
// Edit > Rename Multisample if a real name is wanted before importing audio.
public partial class CreateMultisampleDialog : ThemedWindow
{
    public bool Stereo { get; private set; }

    public CreateMultisampleDialog(uint slot)
    {
        InitializeComponent();
        Title = $"Create New Multisample {slot:D3}";
        PromptLabel.Text = $"Create New Multisample {slot:D3}";
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        Stereo = StereoBox.IsChecked == true;
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
