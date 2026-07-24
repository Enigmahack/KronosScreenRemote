using System.IO;
using System.Windows;
using System.Windows.Input;
using FluentFTP;

namespace KronosScreenRemote;

// Minimal single-purpose remote file picker over FTP, for "Load PCG... From Kronos"
// (Phase 6 of the Librarian rebuild). Deliberately much smaller than FileManagerWindow
// (dual-pane, transfers, rename/delete/drag-drop) — this only needs to browse, pick, and
// download ONE file matching an extension, not manage the whole SD card.
//
// Downloads the selected file itself (over the SAME connection used to browse) before
// closing, rather than handing back a path for the caller to open a SECOND connection to
// fetch — the Kronos's FTP server appears to hold a session open until its own timeout if
// not sent a clean QUIT (see FileManagerWindow.xaml.cs's OnClosing comment), so a caller
// opening a second connection immediately after this one closes risked hanging waiting for
// a session slot. One connection, opened once and cleanly closed once, avoids that entirely.
internal partial class RemoteFilePickerDialog : ThemedWindow
{
    sealed record Entry(string Name, string FullPath, bool IsDirectory)
    {
        public string DisplayName => IsDirectory ? "📁 " + Name : Name;
    }

    readonly AsyncFtpClient _client;
    readonly string _extensionFilter;   // e.g. ".pcg" (case-insensitive)
    string _dir = "/";

    // Set once the user picks a file AND the download over this dialog's own connection
    // succeeds — only then does the dialog close (DialogResult = true).
    public string? DownloadedTempPath { get; private set; }

    public RemoteFilePickerDialog(string host, int port, string user, string pass, string extensionFilter)
    {
        InitializeComponent();
        _client = KronosFtpSession.CreateClient(host, port, user, pass);
        _extensionFilter = extensionFilter;
        LST_Items.SelectionChanged += (_, _) => BTN_Select.IsEnabled = LST_Items.SelectedItem is Entry { IsDirectory: false };
        Loaded += async (_, _) => await ConnectAndRefreshAsync();
        Closed += (_, _) => DisposeInBackground();
    }

    // Disconnect (clean QUIT, so the Kronos's FTP server doesn't hold the session open
    // until its own timeout) then Dispose, both on a background thread, fire-and-forget —
    // never block the UI thread here. This is the exact bug that used to lock up the whole
    // application: FluentFTP's synchronous Dispose() can block waiting on an async cleanup
    // continuation that itself needs the UI thread/dispatcher, and calling it directly from
    // this Closed handler (itself inside the OK button's synchronous click) deadlocked the
    // one thread that could ever complete that continuation. Same fix FileManagerWindow's
    // OnClosing already uses for exactly this reason.
    void DisposeInBackground()
    {
        var client = _client;
        Task.Run(async () =>
        {
            try { await client.Disconnect(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { client.Dispose(); } catch { }
        });
    }

    async Task ConnectAndRefreshAsync()
    {
        TXT_Status.Text = "Connecting…";
        try
        {
            await _client.Connect();
            await RefreshAsync();
        }
        catch (Exception ex) { TXT_Status.Text = $"Connect failed: {ex.Message}"; }
    }

    async Task RefreshAsync()
    {
        TXT_Path.Text = _dir;
        TXT_Status.Text = "Loading…";
        try
        {
            var listing = await _client.GetListing(_dir);
            var entries = listing
                .Select(i => new Entry(i.Name, i.FullName, i.Type == FtpObjectType.Directory))
                .Where(e => e.IsDirectory || e.Name.EndsWith(_extensionFilter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LST_Items.ItemsSource = entries;
            TXT_Status.Text = $"{entries.Count} item(s)";
        }
        catch (Exception ex) { TXT_Status.Text = $"Error: {ex.Message}"; }
    }

    async void OnUp(object sender, RoutedEventArgs e)
    {
        if (_dir is "/" or "") return;
        var trimmed = _dir.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        _dir = lastSlash <= 0 ? "/" : trimmed[..lastSlash];
        await RefreshAsync();
    }

    async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LST_Items.SelectedItem is not Entry entry) return;
        if (entry.IsDirectory) { _dir = entry.FullPath; await RefreshAsync(); }
        else await SelectAndDownloadAsync(entry);
    }

    async void OnSelect(object sender, RoutedEventArgs e)
    {
        if (LST_Items.SelectedItem is Entry { IsDirectory: false } entry)
            await SelectAndDownloadAsync(entry);
    }

    async Task SelectAndDownloadAsync(Entry entry)
    {
        BTN_Select.IsEnabled = false;
        BTN_Cancel.IsEnabled = false;
        BTN_Up.IsEnabled = false;
        TXT_Status.Text = $"Downloading {entry.Name}…";
        try
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "kronos_pcg_cache", entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await _client.DownloadFile(tempPath, entry.FullPath);
            DownloadedTempPath = tempPath;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            TXT_Status.Text = $"Download failed: {ex.Message}";
            BTN_Select.IsEnabled = true;
            BTN_Cancel.IsEnabled = true;
            BTN_Up.IsEnabled = true;
        }
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
