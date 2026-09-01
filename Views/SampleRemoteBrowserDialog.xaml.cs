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
    readonly string _extensionFilter;   // e.g. ".KSC" or ".KMP" (case-insensitive) - unused in folder-push mode
    readonly string _localRoot;         // unused in folder-push mode (nothing is downloaded)
    readonly bool _folderPickMode;
    readonly string? _pushLocalKscPath; // folder-push mode only
    readonly KscCollection? _pushCollection;
    string _dir = "/";

    // Set once the user picks a file AND its whole closure has downloaded successfully
    // over this dialog's own connection - only then does the dialog close. Unset in
    // folder-push mode, where SelectedRemoteDir is the result instead.
    public string? PickedLocalPath { get; private set; }
    public Dictionary<string, string>? RemoteMap { get; private set; }

    // Set (folder-push mode only) to whichever remote directory the user was browsing
    // when they confirmed AND the push into it completed (see SelectFolderAndPushAsync) -
    // "Push to Kronos..." (whole-collection upload) needs a destination FOLDER, not a
    // file to pull, so this mode repurposes the same connect/browse/navigate machinery
    // below, uploading over the SAME connection before closing rather than downloading.
    public string? SelectedRemoteDir { get; private set; }

    // Pull mode: browse for a .KSC/.KMP matching extensionFilter, download its whole
    // dependency closure into localRoot.
    public SampleRemoteBrowserDialog(string host, int port, string user, string pass, string extensionFilter, string localRoot)
    {
        InitializeComponent();
        _client = KronosFtpSession.CreateClient(host, port, user, pass);
        _extensionFilter = extensionFilter;
        _localRoot = localRoot;
        LST_Items.SelectionChanged += (_, _) => BTN_Select.IsEnabled = LST_Items.SelectedItem is Entry { IsDirectory: false };
        Loaded += async (_, _) => await ConnectAndRefreshAsync();
        BlockCloseWhileBusy();
        Closed += (_, _) => DisposeInBackground();
    }

    // Folder-push mode: browse for a DESTINATION FOLDER, then upload localKscPath's
    // whole collection (itself + every listed .KMP + every non-skipped zone's .KSF, via
    // SampleFtpPush) into it over this SAME connection before closing - same one-
    // connection discipline as the pull constructor above, same reason (this class's own
    // header comment). Distinguished from the pull constructor by taking a KscCollection,
    // not by an extra flag - unambiguous overload, no dead pull-only fields to ignore.
    public SampleRemoteBrowserDialog(string host, int port, string user, string pass, string localKscPath, KscCollection collection)
    {
        InitializeComponent();
        _client = KronosFtpSession.CreateClient(host, port, user, pass);
        _extensionFilter = "";
        _localRoot = "";
        _folderPickMode = true;
        _pushLocalKscPath = localKscPath;
        _pushCollection = collection;
        Title = "Select Folder on Kronos";
        BTN_Select.Content = "Select This Folder";
        // Enabled once RefreshAsync's first listing succeeds (see there) - "push into
        // whatever directory I'm currently browsing," not "act on the highlighted row"
        // like the pull mode's per-file gate above, but still gated on actually being
        // connected rather than enabled unconditionally from the start.
        Loaded += async (_, _) => await ConnectAndRefreshAsync();
        BlockCloseWhileBusy();
        Closed += (_, _) => DisposeInBackground();
    }

    // A transfer is running. Both transfer methods already disable every in-dialog button, so
    // this exists for the one route they cannot cover: the title-bar X and Alt+F4, which reach
    // Closed -> DisposeInBackground and dispose the FTP client out from under an active
    // download or upload, tearing the transfer and leaving a half-written workspace.
    bool _busy;

    void BlockCloseWhileBusy() => Closing += (_, e) => e.Cancel = _busy;

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
                // Folder-pick mode: directories only, nothing to filter by file type -
                // there's no file to select, just a destination to navigate into/confirm.
                .Where(e => _folderPickMode
                    ? e.IsDirectory
                    : e.IsDirectory || e.Name.EndsWith(_extensionFilter, StringComparison.OrdinalIgnoreCase))
                // _UserBank.KSC is a live shortcut to Kronos SSD library content, not
                // real sample data (KscCollection.ToBytes already refuses to write one;
                // this keeps it from being picked in the first place, not just rejected
                // after the fact - see SampleEditorViewModel.IsUserBank).
                .Where(e => e.IsDirectory || !e.Name.EndsWith("_UserBank.KSC", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LST_Items.ItemsSource = entries;
            if (_folderPickMode) BTN_Select.IsEnabled = true; // a successful listing means we're actually connected
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
        if (_folderPickMode) { await SelectFolderAndPushAsync(); return; }
        if (LST_Items.SelectedItem is Entry { IsDirectory: false } entry)
            await SelectAndPullAsync(entry);
    }

    // Folder-push mode's counterpart to SelectAndPullAsync below - same shape (disable
    // controls, show progress in TXT_Status, surface partial failures via MessageBox
    // before closing), uploading via SampleFtpPush instead of downloading.
    async Task SelectFolderAndPushAsync()
    {
        _busy = true;
        BTN_Select.IsEnabled = false;
        BTN_Cancel.IsEnabled = false;
        BTN_Up.IsEnabled = false;
        TXT_Status.Text = $"Pushing to '{_dir}'...";
        try
        {
            var failures = await SampleFtpPush.PushClosureAsync(_client, _pushLocalKscPath!, _pushCollection!, _dir,
                msg => TXT_Status.Text = msg);
            SelectedRemoteDir = _dir;
            _busy = false;
            if (failures.Count > 0)
            {
                MessageBox.Show(this,
                    $"{failures.Count} file(s) could not be uploaded to '{_dir}':\n\n{string.Join("\n", failures)}",
                    "Some Files Didn't Upload", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _busy = false;
            TXT_Status.Text = $"Push failed: {ex.Message}";
            BTN_Select.IsEnabled = true;
            BTN_Cancel.IsEnabled = true;
            BTN_Up.IsEnabled = true;
        }
    }

    async Task SelectAndPullAsync(Entry entry)
    {
        _busy = true;
        BTN_Select.IsEnabled = false;
        BTN_Cancel.IsEnabled = false;
        BTN_Up.IsEnabled = false;
        TXT_Status.Text = AppMessages.RemoteSamplePicker.PullingClosure(entry.Name);
        try
        {
            var (localPath, map, failures) = await SampleFtpClosure.PullAsync(_client, entry.FullPath, _localRoot,
                msg => TXT_Status.Text = msg);
            PickedLocalPath = localPath;
            RemoteMap = map;
            _busy = false;
            // Surfaced HERE, before the dialog closes and OpenCollection runs on
            // whatever DID make it to disk - previously a failed .KMP/.KSF download was
            // only ever logged (AppLog.Warn), so the very next thing the user saw was
            // "Loaded 'X.KSC' (N entries)" (the .KSC's own raw entry count, unaffected
            // by what actually downloaded) with an empty or partial tree and no visible
            // explanation why.
            if (failures.Count > 0)
            {
                MessageBox.Show(this,
                    $"{failures.Count} file(s) referenced by '{entry.Name}' could not be downloaded and will be "
                    + $"missing from the loaded collection:\n\n{string.Join("\n", failures)}",
                    "Some Files Didn't Download", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _busy = false;
            TXT_Status.Text = AppMessages.RemoteSamplePicker.DownloadFailed(ex.Message);
            BTN_Select.IsEnabled = true;
            BTN_Cancel.IsEnabled = true;
            BTN_Up.IsEnabled = true;
        }
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
