using System.Windows;
using System.Windows.Input;

namespace KronosScreenRemote;

public partial class AboutWindow : ThemedWindow
{
    public AboutWindow(string? host, int ctrlPort)
    {
        InitializeComponent();

        TXT_ClientVer.Text   = BuildInfo.ClientVersion;
        TXT_ClientBuild.Text = BuildInfo.ClientBuildId;

        TXT_DaemonVer.Text   = "...";
        TXT_DaemonBuild.Text = "...";

        if (host is not null)
            _ = FetchDaemonVersionAsync(host, ctrlPort);
        else
            SetDaemonLabel("not configured");
    }

    async System.Threading.Tasks.Task FetchDaemonVersionAsync(string host, int port)
    {
        try
        {
            var resp = await CtrlQuery.QueryAsync(host, port, "VERSION", timeoutMs: 2000);

            await Dispatcher.InvokeAsync(() =>
            {
                if (resp is null || !resp.StartsWith("VER="))
                {
                    SetDaemonLabel("not reachable");
                    return;
                }

                // "VER=1.1.0 BUILD=abc1234"
                string ver   = "?";
                string build = "?";
                foreach (var part in resp.Split(' '))
                {
                    if (part.StartsWith("VER="))   ver   = part[4..];
                    if (part.StartsWith("BUILD=")) build = part[6..];
                }

                TXT_DaemonVer.Text   = ver;
                TXT_DaemonBuild.Text = build;
            });
        }
        catch (Exception ex)
        {
            // Fire-and-forget task - swallow so it can't become an UnobservedTaskException,
            // and leave the labels in a sensible state instead of a stale "...".
            AppLog.Debug($"[about] daemon version fetch failed: {ex.Message}");
            await Dispatcher.InvokeAsync(() => SetDaemonLabel("not reachable"));
        }
    }

    void SetDaemonLabel(string msg)
    {
        TXT_DaemonVer.Text   = msg;
        TXT_DaemonBuild.Text = msg;
        TXT_DaemonVer.Foreground   = System.Windows.Media.Brushes.DimGray;
        TXT_DaemonBuild.Foreground = System.Windows.Media.Brushes.DimGray;
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // Shared by every link (both GitHub repo lines, the mailto: email line, and the Donate
    // button) - each carries its own target in Tag. MouseLeftButtonDown and Click use different
    // event-arg types, so each keeps its own thin handler; both just forward to OpenLinkFromTag.
    void OnLinkClick(object sender, MouseButtonEventArgs e) => OpenLinkFromTag(sender);
    void OnDonateClick(object sender, RoutedEventArgs e)    => OpenLinkFromTag(sender);

    // Same UseShellExecute pattern MainWindow's own "Check for Updates"/"Report Issue" menu
    // items already use to open a URL in the system default browser (or, for mailto:, the
    // default mail client).
    static void OpenLinkFromTag(object sender)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Debug($"[about] failed to open link '{url}': {ex.Message}"); }
    }
}
