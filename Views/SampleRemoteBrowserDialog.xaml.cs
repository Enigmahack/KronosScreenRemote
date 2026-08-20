using System.Windows;
using System.Windows.Input;
using FluentFTP;

namespace KronosScreenRemote;

// Sample Editor's remote browser: like RemoteFilePickerDialog (single-file PCG pick),
// but picking a .KSC/.KMP here downloads its whole dependency closure over the SAME
// connection before closing - the .KSC + every listed .KMP + every non-skipped zone's
// .KSF - since a Sample Editor "load" is meaningless without the referenced content
// that goes with it. Same one-connection discipline as RemoteFilePickerDialog for the
// same reason (see its own comment): opening a second connection right after this one
// closes risked hanging on the Kronos's FTP server.
internal partial class SampleRemoteBrowserDialog : ThemedWindow
{
    sealed record Entry(string Name, string FullPath, bool IsDirectory)
    {
        public string DisplayName => IsDirectory ? "📁 " + Name : Name;
    }

    readonly AsyncFtpClient _client;
    readonly string _extensionFilter;   // e.g. ".KSC" or ".KMP" (case-insensitive)
    readonly string _localRoot;
    string _dir = "/";

    // Set once the user picks a file AND its whole closure has downloaded successfully
    // over this dialog's own connection - only then does the dialog close.
    public string? PickedLocalPath { get; private set; }
    public Dictionary<string, string>? RemoteMap { get; private set; }

    public SampleRemoteBrowserDialog(string host, int port, string user, string pass, string extensionFilter, string localRoot)
    {
        InitializeComponent();
        _client = KronosFtpSession.CreateClient(host, port, user, pass);
        _extensionFilter = extensionFilter;
        _localRoot = localRoot;
        LST_Items.SelectionChanged += (_, _) => BTN_Select.IsEnabled = LST_Items.SelectedItem is Entry { IsDirectory: false };
        Loaded += async (_, _) => await ConnectAndRefreshAsync();
        Closed += (_, _) => DisposeInBackground();
    }

    // Same fire-and-forget background disconnect+dispose as RemoteFilePickerDialog -
    // FluentFTP's synchronous Dispose() can block on an async cleanup continuation that
    // itself needs the UI thread, so doing this inline in a Closed handler can deadlock.
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
        TXT_Status.Text = AppMessages.RemoteSamplePicker.Connecting;
        try
        {
            await _client.Connect();
            await RefreshAsync();
        }
        catch (Exception ex) { TXT_Status.Text = AppMessages.RemoteSamplePicker.ConnectFailed(ex.Message); }
    }

    async Task RefreshAsync()
    {
        TXT_Path.Text = _dir;
        TXT_Status.Text = AppMessages.RemoteSamplePicker.Loading;
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
            TXT_Status.Text = AppMessages.RemoteSamplePicker.ItemCount(entries.Count);
        }
        catch (Exception ex) { TXT_Status.Text = AppMessages.RemoteSamplePicker.Error(ex.Message); }
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
        else await SelectAndPullAsync(entry);
    }

    async void OnSelect(object sender, RoutedEventArgs e)
    {
        if (LST_Items.SelectedItem is Entry { IsDirectory: false } entry)
            await SelectAndPullAsync(entry);
    }

    async Task SelectAndPullAsync(Entry entry)
    {
        BTN_Select.IsEnabled = false;
        BTN_Cancel.IsEnabled = false;
        BTN_Up.IsEnabled = false;
        TXT_Status.Text = AppMessages.RemoteSamplePicker.PullingClosure(entry.Name);
        try
        {
            var (localPath, map) = await SampleFtpClosure.PullAsync(_client, entry.FullPath, _localRoot,
                msg => TXT_Status.Text = msg);
            PickedLocalPath = localPath;
            RemoteMap = map;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            TXT_Status.Text = AppMessages.RemoteSamplePicker.DownloadFailed(ex.Message);
            BTN_Select.IsEnabled = true;
            BTN_Cancel.IsEnabled = true;
            BTN_Up.IsEnabled = true;
        }
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
