using System.Windows;

namespace KronosScreenRemote;

public partial class AboutWindow : Window
{
    public AboutWindow(string? host, int ctrlPort)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        TXT_ClientVer.Text   = BuildInfo.ClientVersion;
        TXT_ClientBuild.Text = BuildInfo.ClientBuildId;

        TXT_DaemonVer.Text   = "…";
        TXT_DaemonBuild.Text = "…";

        if (host is not null)
            _ = FetchDaemonVersionAsync(host, ctrlPort);
        else
            SetDaemonLabel("not configured");
    }

    async System.Threading.Tasks.Task FetchDaemonVersionAsync(string host, int port)
    {
        var resp = await CtrlClient.QueryAsync(host, port, "VERSION", timeoutMs: 2000);

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

    void SetDaemonLabel(string msg)
    {
        TXT_DaemonVer.Text   = msg;
        TXT_DaemonBuild.Text = msg;
        TXT_DaemonVer.Foreground   = System.Windows.Media.Brushes.DimGray;
        TXT_DaemonBuild.Foreground = System.Windows.Media.Brushes.DimGray;
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
