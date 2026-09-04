using System.Windows;
using FluentFTP;

namespace KronosScreenRemote;

// "Properties" for a single FTP entry - SampleRemoteBrowserDialog's right-click menu.
// Everything except Size shows immediately from the FtpListItem the browser's own
// directory listing already produced - no extra round trip. A directory's total size
// is NOT in that listing (a Unix LIST line reports the directory inode's own size, not
// its recursive contents, and BusyBox's ftpd is no exception) so it's computed on
// demand via the Calculate button rather than eagerly walking a potentially huge SD
// card tree on every Properties click - FluentFTP's FtpListOption.Recursive still
// means one GetListing per subdirectory under the hood, which can be genuinely slow
// over the Kronos's own embedded ftpd for a full sample library.
internal partial class FtpPropertiesDialog : ThemedWindow
{
    readonly AsyncFtpClient _client;
    readonly string _path;
    readonly bool _isDirectory;

    public FtpPropertiesDialog(AsyncFtpClient client, string path, bool isDirectory, FtpListItem item)
    {
        InitializeComponent();
        _client = client;
        _path = path;
        _isDirectory = isDirectory;

        NameText.Text     = item.Name;
        PathText.Text     = path;
        TypeText.Text     = isDirectory ? "Directory" : "File";
        ModifiedText.Text = item.Modified == default ? "(not reported by server)" : item.Modified.ToString("yyyy-MM-dd HH:mm:ss");

        PermissionsText.Text = string.IsNullOrEmpty(item.RawPermissions)
            ? "(not reported by server)"
            : $"{item.RawPermissions} ({item.Chmod})"
                + (string.IsNullOrEmpty(item.RawOwner) ? "" : $"   owner: {item.RawOwner}")
                + (string.IsNullOrEmpty(item.RawGroup) ? "" : $"   group: {item.RawGroup}");

        if (isDirectory)
        {
            SizeText.Text = "(not calculated)";
            BtnCalculate.Visibility = Visibility.Visible;
        }
        else
        {
            SizeText.Text = FormatBytes(item.Size);
            BtnCalculate.Visibility = Visibility.Collapsed;
        }
    }

    async void OnCalculate(object sender, RoutedEventArgs e)
    {
        BtnCalculate.IsEnabled = false;
        SizeText.Text = "Calculating (this can take a while on a large folder)...";
        try
        {
            var items = await _client.GetListing(_path, FtpListOption.Recursive);
            long total = items.Where(i => i.Type == FtpObjectType.File).Sum(i => i.Size);
            int fileCount = items.Count(i => i.Type == FtpObjectType.File);
            int dirCount  = items.Count(i => i.Type == FtpObjectType.Directory);
            SizeText.Text = $"{FormatBytes(total)}  ({fileCount} file(s), {dirCount} folder(s))";
        }
        catch (Exception ex)
        {
            SizeText.Text = $"Couldn't calculate: {ex.Message}";
            BtnCalculate.IsEnabled = true;
        }
    }

    static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]} ({bytes:N0} bytes)";
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
