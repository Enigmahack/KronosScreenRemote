using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluentFTP;
// Note: System.Windows.Media is kept for SolidColorBrush used in RubberBandAdorner

namespace KronosScreenRemote;

public partial class FileManagerWindow : Window
{
    // ── File entry model ──────────────────────────────────────────────────────
    record FileEntry(string Name, string FullPath, bool IsDirectory, long Bytes, DateTime Modified)
    {
        public string DisplayName => IsDirectory ? "📁 " + Name : Name;
        public string SizeText    => IsDirectory ? "<DIR>" :
            Bytes < 1_024     ? $"{Bytes} B"  :
            Bytes < 1_048_576 ? $"{Bytes / 1_024} KB" :
                                $"{Bytes / 1_048_576} MB";
        public string DateText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    }

    record DragPayload(bool FromRemote, IReadOnlyList<FileEntry> Items);
    record ClipboardPayload(bool IsCut, bool FromRemote, IReadOnlyList<FileEntry> Items);
    record DriveItem(string RootPath, string Display)
    {
        public override string ToString() => Display;
    }

    enum ConflictAction { Rename, Overwrite, Skip, Cancel }
    enum SortColumn     { Name, Size, Modified }
    record ConflictResult(ConflictAction Action, string Name, bool ApplyToAll);

    // ── Conflict dialog (Rename / Overwrite / Skip / Cancel) ─────────────────
    sealed class ConflictDialog : Window
    {
        public ConflictAction Action     { get; private set; } = ConflictAction.Cancel;
        public string         ResultName { get; private set; }
        public bool           ApplyToAll { get; private set; }

        public ConflictDialog(string fileName, Window owner)
        {
            ResultName            = SuggestName(fileName);
            Owner                 = owner;
            Title                 = "File Already Exists";
            ResizeMode            = ResizeMode.NoResize;
            SizeToContent         = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background            = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));
            WindowTheme.ApplyDarkCaption(this);

            var nameBox = new TextBox
            {
                Text        = ResultName,
                MinWidth    = 300,
                IsReadOnly  = false,
                Background  = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d)),
                Foreground  = Brushes.White,
                CaretBrush  = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Padding     = new Thickness(4, 3, 4, 3),
                Margin      = new Thickness(0, 0, 0, 12)
            };

            var applyAllBox = new CheckBox
            {
                Content    = "Do this for all remaining conflicts",
                Foreground = Brushes.White,
                Margin     = new Thickness(0, 0, 0, 14)
            };

            var btnBg   = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a));
            var btnBord = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            Button Btn(string label, ConflictAction act)
            {
                var b = new Button
                {
                    Content     = label,
                    MinWidth    = 80,
                    Padding     = new Thickness(8, 4, 8, 4),
                    Margin      = new Thickness(0, 0, 8, 0),
                    Background  = btnBg,
                    Foreground  = Brushes.White,
                    BorderBrush = btnBord
                };
                b.Click += (_, _) =>
                {
                    Action     = act;
                    ApplyToAll = applyAllBox.IsChecked == true;
                    if (act == ConflictAction.Rename)
                        ResultName = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : fileName;
                    Close();
                };
                return b;
            }

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            btnRow.Children.Add(Btn("Rename",    ConflictAction.Rename));
            btnRow.Children.Add(Btn("Overwrite", ConflictAction.Overwrite));
            btnRow.Children.Add(Btn("Skip",      ConflictAction.Skip));
            btnRow.Children.Add(Btn("Cancel",    ConflictAction.Cancel));

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text         = $"\"{fileName}\" already exists at the destination.",
                Foreground   = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 380,
                Margin       = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new TextBlock
            {
                Text       = "New name:",
                Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
                Margin     = new Thickness(0, 0, 0, 4)
            });
            root.Children.Add(nameBox);
            root.Children.Add(applyAllBox);
            root.Children.Add(btnRow);

            Content = root;
        }

        // Called by transfer methods when applying a remembered Rename to a different filename
        internal static string SuggestName(string name)
        {
            var ext  = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            return $"{stem} (Copy){ext}";
        }
    }

    // Rubber-band selection adorner
    sealed class RubberBandAdorner : Adorner
    {
        Rect _rect;
        static readonly Pen   _pen  = new(new SolidColorBrush(Color.FromArgb(180, 60, 140, 220)), 1);
        static readonly Brush _fill = new SolidColorBrush(Color.FromArgb(35,  60, 140, 220));
        public RubberBandAdorner(UIElement el) : base(el) { IsHitTestVisible = false; }
        public void SetRect(Rect r) { _rect = r; InvalidateVisual(); }
        protected override void OnRender(DrawingContext ctx) => ctx.DrawRectangle(_fill, _pen, _rect);
    }

    // ── State ─────────────────────────────────────────────────────────────────
    readonly string _host;
    readonly int    _ftpPort;
    readonly string _user;
    readonly string _pass;

    AsyncFtpClient? _ftp;
    string _remotePath = "/";
    string _localPath  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    readonly ObservableCollection<FileEntry> _remoteItems = new();
    readonly ObservableCollection<FileEntry> _localItems  = new();

    bool _busy;
    bool _suppressDriveChange;

    // Clipboard (cut/copy/paste)
    ClipboardPayload? _clipboard;

    // Drag-drop
    const string DragDataFormat = "KronosScreenRemote.FileEntries";
    ListView?    _dragSource;
    Point        _dragStart;
    FileEntry?   _deferredSelectEntry; // item to solo-select on mouseup when no drag occurred

    // Rubber-band select
    bool               _rubberBanding;
    Point              _rubberOrigin;
    RubberBandAdorner? _rubberAdorner;
    ListView?          _rubberList;
    // Dwell-to-navigate (hover a folder or ↑ button during drag to auto-navigate)
    DispatcherTimer? _dwellTimer;
    object?          _dwellTarget; // FileEntry (folder) or Button (↑ button)
    ListView?        _dwellList;

    // Drag-scroll (auto-scroll list near edges while a file drag is in progress)
    const double DragScrollHotzone  = 40.0;
    const double DragScrollMaxSpeed = 14.0;  // scroll units per 50 ms tick
    readonly DispatcherTimer _dragScrollTimer  = new();
    ScrollViewer?            _dragScrollViewer;
    double                   _dragScrollDelta;
    ScrollViewer?            _localScrollViewer;
    ScrollViewer?            _remoteScrollViewer;

    // Column sort
    SortColumn     _localSortCol  = SortColumn.Name;
    bool           _localSortAsc  = true;
    SortColumn     _remoteSortCol = SortColumn.Name;
    bool           _remoteSortAsc = true;
    GridViewColumn _localNameCol  = null!;
    GridViewColumn _localSizeCol  = null!;
    GridViewColumn _localDateCol  = null!;
    GridViewColumn _remoteNameCol = null!;
    GridViewColumn _remoteSizeCol = null!;
    GridViewColumn _remoteDateCol = null!;

    // ── Constructor ───────────────────────────────────────────────────────────
    public FileManagerWindow(string host, int ftpPort, string user, string pass)
    {
        _host    = host;
        _ftpPort = ftpPort;
        _user    = user;
        _pass    = pass;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);
        LocalList.ItemsSource  = _localItems;
        RemoteList.ItemsSource = _remoteItems;

        foreach (var lv in new[] { LocalList, RemoteList })
        {
            lv.PreviewMouseLeftButtonDown += OnListPreviewMouseDown;
            lv.PreviewMouseMove           += OnListPreviewMouseMove;
            lv.PreviewMouseLeftButtonUp   += OnListPreviewMouseUp;
            lv.MouseLeave  += OnListMouseLeave;
            lv.AllowDrop   = true;
            lv.DragOver   += OnListDragOver;
            lv.DragLeave  += OnListDragLeave;
        }
        LocalList.Drop  += OnLocalDrop;
        RemoteList.Drop += OnRemoteDrop;

        LocalList.PreviewMouseRightButtonDown  += (s, e) => PrepareContextMenu(LocalList,  isRemote: false, e);
        RemoteList.PreviewMouseRightButtonDown += (s, e) => PrepareContextMenu(RemoteList, isRemote: true,  e);

        // Column sort — cache GridViewColumn refs and stamp initial ▲ on Name header
        var lg = (GridView)LocalList.View;
        var rg = (GridView)RemoteList.View;
        _localNameCol  = lg.Columns[0];
        _localSizeCol  = lg.Columns[1];
        _localDateCol  = lg.Columns[2];
        _remoteNameCol = rg.Columns[0];
        _remoteSizeCol = rg.Columns[1];
        _remoteDateCol = rg.Columns[2];
        UpdateLocalHeaders();
        UpdateRemoteHeaders();
        LocalList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(OnLocalColumnHeaderClick));
        RemoteList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(OnRemoteColumnHeaderClick));

        // Drag-scroll timer
        _dragScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _dragScrollTimer.Tick    += OnDragScrollTick;

        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    async void OnLoaded(object s, RoutedEventArgs e)
    {
        _localScrollViewer  = GetScrollViewer(LocalList);
        _remoteScrollViewer = GetScrollViewer(RemoteList);
        PopulateLocalDrives();
        _ftp = KronosFtpSession.CreateClient(_host, _ftpPort, _user, _pass);
        try
        {
            SetStatus("Connecting to Kronos FTP…");
            await Task.Run(() => _ftp.Connect(CancellationToken.None));
            SetStatus("Connected.");
            await RefreshBothAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"FTP connect failed: {ex.Message}");
        }
    }

    void OnClosing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        // Break the Owner link before closing so WPF doesn't minimize the parent
        // when this window had focus (known WPF owner-activation bug).
        Owner = null;

        // Hand the client off to a background thread so the UI thread isn't blocked.
        // Send QUIT first so BusyBox ftpd cleanly removes the session — without it the
        // server holds the session open until its own timeout, accumulating ghost sessions.
        var ftp = _ftp;
        _ftp = null;
        if (ftp != null)
            Task.Run(async () =>
            {
                try { await ftp.Disconnect(CancellationToken.None).ConfigureAwait(false); } catch { }
                try { ftp.Dispose(); } catch { }
            });
    }

    // ── Navigation ────────────────────────────────────────────────────────────
    async void OnRemoteDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (RemoteList.SelectedItem is not FileEntry item || !item.IsDirectory) return;
        _remotePath = item.FullPath;
        await RefreshRemoteAsync();
    }

    void OnLocalDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (LocalList.SelectedItem is not FileEntry item || !item.IsDirectory) return;
        _localPath = item.FullPath;
        RefreshLocal();
    }

    async void OnRemoteUp(object s, RoutedEventArgs e)
    {
        var parent = GetFtpParent(_remotePath);
        if (parent == _remotePath) return;
        _remotePath = parent;
        await RefreshRemoteAsync();
    }

    void OnLocalUp(object s, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_localPath)?.FullName;
        if (parent == null) return;
        _localPath = parent;
        RefreshLocal();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────
    async void OnRemoteRefresh(object s, RoutedEventArgs e) => await RefreshRemoteAsync();
    void       OnLocalRefresh (object s, RoutedEventArgs e) => RefreshLocal();

    async Task RefreshBothAsync() { await RefreshRemoteAsync(); RefreshLocal(); }

    async Task RefreshRemoteAsync()
    {
        if (!await EnsureConnectedAsync()) return;
        RemotePathBox.Text = _remotePath;
        SetStatus($"Loading {_remotePath}…");
        try
        {
            var listing = await _ftp!.GetListing(_remotePath);
            var entries = listing
                .Select(i => new FileEntry(i.Name, i.FullName,
                    i.Type == FtpObjectType.Directory, i.Size, i.Modified))
                .ToList();
            _remoteItems.Clear();
            foreach (var entry in entries) _remoteItems.Add(entry);
            ApplySort(_remoteItems, _remoteSortCol, _remoteSortAsc);
            SetStatus($"{entries.Count} item(s) in {_remotePath}");
        }
        catch (Exception ex) { SetStatus($"Error listing remote: {ex.Message}"); }
    }

    void RefreshLocal()
    {
        LocalPathBox.Text = _localPath;
        SyncDriveCombo();
        try
        {
            _localItems.Clear();
            foreach (var d in Directory.GetDirectories(_localPath).Select(p => new DirectoryInfo(p)))
                _localItems.Add(new FileEntry(d.Name, d.FullName, true, 0, d.LastWriteTime));
            foreach (var f in Directory.GetFiles(_localPath).Select(p => new FileInfo(p)))
                _localItems.Add(new FileEntry(f.Name, f.FullName, false, f.Length, f.LastWriteTime));
            ApplySort(_localItems, _localSortCol, _localSortAsc);
            SetStatus($"{_localItems.Count} item(s) in {_localPath}");
        }
        catch (Exception ex) { SetStatus($"Error listing local: {ex.Message}"); }
    }

    // ── Upload (local → Kronos) ───────────────────────────────────────────────
    async void OnUpload(object s, RoutedEventArgs e)
    {
        var items = LocalList.SelectedItems.Cast<FileEntry>().Where(f => !f.IsDirectory).ToList();
        if (items.Count == 0) { SetStatus("Select one or more local files to upload."); return; }
        await UploadItemsAsync(items);
    }

    async Task UploadItemsAsync(IList<FileEntry> items)
    {
        if (!await EnsureConnectedAsync()) return;
        var remoteNames = _remoteItems.Where(f => !f.IsDirectory)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetBusy(true, $"Uploading {items.Count} file(s)…");
        int done = 0;
        ConflictResult? remembered = null;
        ConflictResult Resolve(string fn)
        {
            if (remembered != null)
                return remembered.Action == ConflictAction.Rename
                    ? remembered with { Name = ConflictDialog.SuggestName(fn) }
                    : remembered;
            var r = AskConflict(fn);
            if (r.ApplyToAll) remembered = r;
            return r;
        }
        foreach (var (local, idx) in items.Select((x, i) => (x, i)))
        {
            var fileName = Path.GetFileName(local.FullPath);
            var dest     = $"{_remotePath.TrimEnd('/')}/{fileName}";
            if (remoteNames.Contains(fileName))
            {
                var r = Resolve(fileName);
                if (r.Action == ConflictAction.Cancel) break;
                if (r.Action == ConflictAction.Skip)   continue;
                if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = $"{_remotePath.TrimEnd('/')}/{fileName}"; }
            }
            try
            {
                var progress = new Progress<FtpProgress>(p => Dispatcher.InvokeAsync(() =>
                {
                    TransferProgress.Value = p.Progress;
                    SetStatus($"[{idx + 1}/{items.Count}] {local.Name} — {p.Progress:F0}%");
                }));
                await _ftp!.UploadFile(local.FullPath, dest, FtpRemoteExists.Overwrite,
                                       createRemoteDir: true, progress: progress);
                done++;
            }
            catch (Exception ex) { SetStatus($"Failed {local.Name}: {ex.Message}"); }
        }
        await RefreshRemoteAsync();
        SetStatus($"Uploaded {done}/{items.Count} file(s) → {_remotePath}");
        SetBusy(false);
    }

    // ── Download (Kronos → local) ─────────────────────────────────────────────
    async void OnDownload(object s, RoutedEventArgs e)
    {
        var items = RemoteList.SelectedItems.Cast<FileEntry>().Where(f => !f.IsDirectory).ToList();
        if (items.Count == 0) { SetStatus("Select one or more Kronos files to download."); return; }
        await DownloadItemsAsync(items);
    }

    async Task DownloadItemsAsync(IList<FileEntry> items)
    {
        if (!await EnsureConnectedAsync()) return;
        SetBusy(true, $"Downloading {items.Count} file(s)…");
        int done = 0;
        ConflictResult? remembered = null;
        ConflictResult Resolve(string fn)
        {
            if (remembered != null)
                return remembered.Action == ConflictAction.Rename
                    ? remembered with { Name = ConflictDialog.SuggestName(fn) }
                    : remembered;
            var r = AskConflict(fn);
            if (r.ApplyToAll) remembered = r;
            return r;
        }
        foreach (var (remote, idx) in items.Select((x, i) => (x, i)))
        {
            var fileName = Path.GetFileName(remote.FullPath);
            var dest     = Path.Combine(_localPath, fileName);
            if (File.Exists(dest))
            {
                var r = Resolve(fileName);
                if (r.Action == ConflictAction.Cancel) break;
                if (r.Action == ConflictAction.Skip)   continue;
                if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = Path.Combine(_localPath, fileName); }
            }
            try
            {
                var progress = new Progress<FtpProgress>(p => Dispatcher.InvokeAsync(() =>
                {
                    TransferProgress.Value = p.Progress;
                    SetStatus($"[{idx + 1}/{items.Count}] {remote.Name} — {p.Progress:F0}%");
                }));
                await _ftp!.DownloadFile(dest, remote.FullPath, FtpLocalExists.Overwrite,
                                         FtpVerify.None, progress);
                done++;
            }
            catch (Exception ex) { SetStatus($"Failed {remote.Name}: {ex.Message}"); }
        }
        RefreshLocal();
        SetStatus($"Downloaded {done}/{items.Count} file(s) → {_localPath}");
        SetBusy(false);
    }

    // ── New Folder ────────────────────────────────────────────────────────────
    async void OnRemoteNewFolder(object s, RoutedEventArgs e)
    {
        var name = PromptInput("New folder name:", "NewFolder");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!await EnsureConnectedAsync()) return;
        var path = $"{_remotePath.TrimEnd('/')}/{name}";
        try { await _ftp!.CreateDirectory(path); await RefreshRemoteAsync(); SetStatus($"Created {path}"); }
        catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
    }

    void OnLocalNewFolder(object s, RoutedEventArgs e)
    {
        var name = PromptInput("New folder name:", "NewFolder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(_localPath, name);
        try { Directory.CreateDirectory(path); RefreshLocal(); SetStatus($"Created {path}"); }
        catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
    }

    // ── Delete ────────────────────────────────────────────────────────────────
    async void OnRemoteDelete(object s, RoutedEventArgs e)
    {
        var items = RemoteList.SelectedItems.Cast<FileEntry>().ToList();
        if (items.Count == 0) { SetStatus("Select items to delete."); return; }
        if (MessageBox.Show($"Delete {items.Count} item(s) from Kronos?", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await EnsureConnectedAsync()) return;
        int done = 0;
        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory) await _ftp!.DeleteDirectory(item.FullPath);
                else                  await _ftp!.DeleteFile(item.FullPath);
                done++;
            }
            catch (Exception ex) { SetStatus($"Failed {item.Name}: {ex.Message}"); }
        }
        await RefreshRemoteAsync();
        SetStatus($"Deleted {done}/{items.Count} item(s).");
    }

    void OnLocalDelete(object s, RoutedEventArgs e)
    {
        var items = LocalList.SelectedItems.Cast<FileEntry>().ToList();
        if (items.Count == 0) { SetStatus("Select items to delete."); return; }
        if (MessageBox.Show($"Delete {items.Count} item(s)?", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int done = 0;
        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory) Directory.Delete(item.FullPath, recursive: true);
                else                  File.Delete(item.FullPath);
                done++;
            }
            catch (Exception ex) { SetStatus($"Failed {item.Name}: {ex.Message}"); }
        }
        RefreshLocal();
        SetStatus($"Deleted {done}/{items.Count} item(s).");
    }

    // ── Rename ────────────────────────────────────────────────────────────────
    async void OnRemoteRename(object s, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItems.Count != 1) { SetStatus("Select exactly one item to rename."); return; }
        var item    = (FileEntry)RemoteList.SelectedItem!;
        var newName = PromptInput("New name:", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        if (!await EnsureConnectedAsync()) return;
        var newPath = $"{GetFtpParent(item.FullPath).TrimEnd('/')}/{newName}";
        try { await _ftp!.Rename(item.FullPath, newPath); await RefreshRemoteAsync(); SetStatus($"Renamed → {newName}"); }
        catch (Exception ex) { SetStatus($"Rename failed: {ex.Message}"); }
    }

    void OnLocalRename(object s, RoutedEventArgs e)
    {
        if (LocalList.SelectedItems.Count != 1) { SetStatus("Select exactly one item to rename."); return; }
        var item    = (FileEntry)LocalList.SelectedItem!;
        var newName = PromptInput("New name:", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        var newPath = Path.Combine(Path.GetDirectoryName(item.FullPath) ?? _localPath, newName);
        try
        {
            if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
            else                  File.Move(item.FullPath, newPath);
            RefreshLocal();
            SetStatus($"Renamed → {newName}");
        }
        catch (Exception ex) { SetStatus($"Rename failed: {ex.Message}"); }
    }

    // ── Drag-to-select (rubber-band) ──────────────────────────────────────────
    void OnListPreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        var lv    = (ListView)s;
        var entry = GetEntryAt(lv, e.GetPosition(lv));
        _dragSource = null;

        if (entry != null)
        {
            // Any item click: set up potential file drag
            _dragSource = lv;
            _dragStart  = e.GetPosition(lv);

            // Plain click on an already-selected item in a multi-selection: suppress WPF's
            // immediate selection collapse so we can tell click-to-deselect from drag-group.
            bool modifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            if (!modifier && lv.SelectedItems.Contains(entry) && lv.SelectedItems.Count > 1)
            {
                _deferredSelectEntry = entry;
                e.Handled            = true; // WPF won't deselect others until mouseup
                lv.Focus();
            }
        }
        else
        {
            // Empty space only: start rubber-band (also clears selection if no Ctrl)
            BeginRubberBand(lv, e.GetPosition(lv));
        }
    }

    void OnListPreviewMouseMove(object s, MouseEventArgs e)
    {
        var lv = (ListView)s;

        if (_rubberBanding && _rubberList == lv)
        {
            var pos  = e.GetPosition(lv);
            var rect = new Rect(_rubberOrigin, pos);
            _rubberAdorner?.SetRect(rect);
            UpdateRubberBandSelection(lv, rect);
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (_dragSource == lv)
        {
            var pos = e.GetPosition(lv);
            if (Math.Abs(pos.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragSource          = null;
                _deferredSelectEntry = null; // drag wins — don't collapse selection on mouseup
                InitiateFileDrag(lv);
            }
        }
    }

    void OnListPreviewMouseUp(object s, MouseButtonEventArgs e) { /* handled by Window override */ }

    void BeginRubberBand(ListView lv, Point start)
    {
        if (_rubberBanding) EndRubberBand(); // clean up any orphaned state
        _rubberList    = lv;
        _rubberOrigin  = start;
        _rubberBanding = true;
        // No CaptureMouse — it fights with ListViewItem's press-state tracking and
        // causes rubber-band to silently fail. PreviewMouseMove fires regardless because
        // it's a tunneling event; OnPreviewMouseLeftButtonUp (Window override) ensures
        // we always terminate even if the button is released outside the list.

        var layer = AdornerLayer.GetAdornerLayer(lv);
        if (layer != null)
        {
            _rubberAdorner = new RubberBandAdorner(lv);
            layer.Add(_rubberAdorner);
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            lv.SelectedItems.Clear();
    }

    void EndRubberBand()
    {
        if (!_rubberBanding) return;
        _rubberBanding = false;

        if (_rubberAdorner != null && _rubberList != null)
        {
            AdornerLayer.GetAdornerLayer(_rubberList)?.Remove(_rubberAdorner);
            _rubberAdorner = null;
        }
        _rubberList = null;
    }

    // Window-level override guarantees rubber-band ends regardless of where the
    // button is released (including outside the ListView).
    // ── Keyboard shortcuts (Ctrl+C/X/V/A, Del, F2, F5, Backspace, Enter) ──────
    // WPF routes keyboard events only within the focused window's visual tree, so
    // these never fire in the main window and macros never fire here — naturally
    // isolated without any extra guards.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt  = (Keyboard.Modifiers & ModifierKeys.Alt)     != 0;
        if (alt) return; // leave Alt+F4, Alt+Tab etc. untouched

        bool remoteHas = RemoteList.IsKeyboardFocusWithin;
        bool localHas  = LocalList.IsKeyboardFocusWithin;
        bool anyPane   = remoteHas || localHas;
        bool isRemote  = remoteHas;
        var  lv        = remoteHas ? RemoteList : localHas ? LocalList : (ListView?)null;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C when anyPane:
                    DoCopy(lv!, isRemote); e.Handled = true; break;

                case Key.X when anyPane:
                    DoCut(lv!, isRemote);  e.Handled = true; break;

                case Key.V when anyPane:
                    _ = DoPasteAsync(isRemote); e.Handled = true; break;

                case Key.A when anyPane:
                    lv!.SelectAll(); e.Handled = true; break;
            }
            return;
        }

        if (e.IsRepeat) return;

        switch (e.Key)
        {
            case Key.Delete when anyPane:
                if (isRemote) OnRemoteDelete(null!, null!);
                else          OnLocalDelete(null!, null!);
                e.Handled = true; break;

            case Key.F2 when anyPane:
                if (isRemote) OnRemoteRename(null!, null!);
                else          OnLocalRename(null!, null!);
                e.Handled = true; break;

            case Key.F5:
                if      (remoteHas) _ = RefreshRemoteAsync();
                else if (localHas)  RefreshLocal();
                else                { _ = RefreshRemoteAsync(); RefreshLocal(); }
                e.Handled = true; break;

            case Key.Back when anyPane:
                if (isRemote) OnRemoteUp(null!, null!);
                else          OnLocalUp(null!, null!);
                e.Handled = true; break;

            case Key.Return when anyPane:
                // Navigate into the selected folder (mirrors double-click)
                if (lv!.SelectedItem is FileEntry { IsDirectory: true } dir)
                {
                    if (isRemote) { _remotePath = dir.FullPath; _ = RefreshRemoteAsync(); }
                    else          { _localPath  = dir.FullPath; RefreshLocal(); }
                }
                e.Handled = true; break;
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_rubberBanding) EndRubberBand();

        // Plain click (no drag) on a multi-selected item: collapse selection now
        if (_dragSource != null && _deferredSelectEntry != null)
        {
            _dragSource.SelectedItems.Clear();
            _dragSource.SelectedItems.Add(_deferredSelectEntry);
        }

        _deferredSelectEntry = null;
        _dragSource          = null;
    }

    void OnListMouseLeave(object s, MouseEventArgs e)
    {
        var lv = (ListView)s;
        if (_rubberBanding && _rubberList == lv) EndRubberBand();
        // Mouse left without triggering a drag or click — cancel deferred state
        if (_dragSource == lv) { _dragSource = null; _deferredSelectEntry = null; }
    }

    void UpdateRubberBandSelection(ListView lv, Rect selectRect)
    {
        bool addMode = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (!addMode) lv.SelectedItems.Clear();

        foreach (var item in lv.Items)
        {
            var container = lv.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container == null) continue;
            var bounds = container.TransformToAncestor(lv)
                                  .TransformBounds(new Rect(container.RenderSize));
            if (selectRect.IntersectsWith(bounds))
                lv.SelectedItems.Add(item);
        }
    }

    // ── Drag+drop (file transfer) ─────────────────────────────────────────────
    void InitiateFileDrag(ListView lv)
    {
        var items = lv.SelectedItems.Cast<FileEntry>().Where(f => !f.IsDirectory).ToList();
        if (items.Count == 0) return;
        var payload = new DragPayload(lv == RemoteList, items);
        var data    = new DataObject(DragDataFormat, payload);
        DragDrop.DoDragDrop(lv, data, DragDropEffects.Copy | DragDropEffects.Move);
        // Drag ended — guarantee dwell state, scroll, and button highlights are cleaned up
        CancelDwell();
        StopDragScroll();
        BtnLocalUp.ClearValue(BackgroundProperty);
        BtnRemoteUp.ClearValue(BackgroundProperty);
    }

    void OnListDragOver(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDataFormat)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var payload  = (DragPayload)e.Data.GetData(DragDataFormat);
        var lv       = (ListView)s;
        var hovered  = GetEntryAt(lv, e.GetPosition(lv));
        bool samePaneFolder = hovered is { IsDirectory: true } && (lv == RemoteList) == payload.FromRemote;
        e.Effects = samePaneFolder ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;

        if (samePaneFolder) StartDwell(lv, hovered!); // hovered non-null when samePaneFolder
        else                CancelDwell();

        // Drag-scroll: auto-scroll when mouse is near the top or bottom edge
        var pos = e.GetPosition(lv);
        var sv  = lv == LocalList ? _localScrollViewer : _remoteScrollViewer;
        double h = lv.ActualHeight;
        if (sv != null && pos.Y >= 0 && pos.Y < DragScrollHotzone)
        {
            _dragScrollDelta  = -DragScrollMaxSpeed * (1.0 - pos.Y / DragScrollHotzone);
            _dragScrollViewer = sv;
            if (!_dragScrollTimer.IsEnabled) _dragScrollTimer.Start();
        }
        else if (sv != null && pos.Y > h - DragScrollHotzone && pos.Y <= h)
        {
            _dragScrollDelta  = DragScrollMaxSpeed * (1.0 - (h - pos.Y) / DragScrollHotzone);
            _dragScrollViewer = sv;
            if (!_dragScrollTimer.IsEnabled) _dragScrollTimer.Start();
        }
        else
        {
            StopDragScroll();
        }
    }

    void OnListDragLeave(object s, DragEventArgs e) { CancelDwell(); StopDragScroll(); }

    // ↑ button drop targets: hovering navigates up; dropping moves files to the parent
    void OnUpDragOver(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDataFormat)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var payload = (DragPayload)e.Data.GetData(DragDataFormat);
        var btn     = (Button)s;
        bool samePane = (btn == BtnRemoteUp) == payload.FromRemote;
        if (!samePane) { e.Effects = DragDropEffects.None; e.Handled = true; return; }

        e.Effects      = DragDropEffects.Move;
        e.Handled      = true;
        btn.Background = new SolidColorBrush(Color.FromRgb(60, 120, 180));
        StartDwell(btn == BtnRemoteUp ? RemoteList : LocalList, btn);
    }

    void OnUpDragLeave(object s, DragEventArgs e)
    {
        ((Button)s).ClearValue(BackgroundProperty);
        CancelDwell();
    }

    async void OnUpDrop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDataFormat)) return;
        var payload = (DragPayload)e.Data.GetData(DragDataFormat);
        var btn     = (Button)s;
        btn.ClearValue(BackgroundProperty);
        CancelDwell();

        if (btn == BtnRemoteUp && payload.FromRemote)
        {
            var parent = GetFtpParent(_remotePath);
            if (parent == _remotePath) return; // already at root
            _remotePath = parent;
            await RefreshRemoteAsync();
            var items = payload.Items.Where(f => GetFtpParent(f.FullPath) != _remotePath).ToList();
            if (items.Count > 0) await MoveRemoteItemsAsync(items, _remotePath);
        }
        else if (btn == BtnLocalUp && !payload.FromRemote)
        {
            var parent = Directory.GetParent(_localPath)?.FullName;
            if (parent == null) return;
            _localPath = parent;
            RefreshLocal();
            var items = payload.Items
                .Where(f => (Path.GetDirectoryName(f.FullPath) ?? "") != _localPath).ToList();
            if (items.Count > 0) await MoveLocalItemsAsync(items, _localPath);
        }
    }

    // Dwell-to-navigate: hover a folder or ↑ button for 750 ms during a drag
    void StartDwell(ListView lv, object target)
    {
        if (target == _dwellTarget) return; // already timing this target
        CancelDwell();
        _dwellTarget = target;
        _dwellList   = lv;
        _dwellTimer  = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _dwellTimer.Tick += OnDwellTick;
        _dwellTimer.Start();
    }

    async void OnDwellTick(object? s, EventArgs e)
    {
        var target = _dwellTarget;
        var lv     = _dwellList;
        CancelDwell();
        if (lv == null || target == null) return;

        if (target is FileEntry folder && folder.IsDirectory)
        {
            if (lv == RemoteList) { _remotePath = folder.FullPath; await RefreshRemoteAsync(); }
            else                  { _localPath  = folder.FullPath; RefreshLocal(); }
        }
        else if (target is Button btn)
        {
            if (btn == BtnRemoteUp)
            {
                var parent = GetFtpParent(_remotePath);
                if (parent != _remotePath) { _remotePath = parent; await RefreshRemoteAsync(); }
            }
            else
            {
                var parent = Directory.GetParent(_localPath)?.FullName;
                if (parent != null) { _localPath = parent; RefreshLocal(); }
            }
        }
    }

    void CancelDwell()
    {
        _dwellTimer?.Stop();
        _dwellTimer  = null;
        _dwellTarget = null;
        _dwellList   = null;
    }

    async void OnLocalDrop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDataFormat)) return;
        var payload = (DragPayload)e.Data.GetData(DragDataFormat);
        var items   = payload.Items.ToList();

        // Drop onto a subfolder in this pane → move into it
        var targetFolder = GetEntryAt(LocalList, e.GetPosition(LocalList));
        if (!payload.FromRemote && targetFolder is { IsDirectory: true })
        {
            await MoveLocalItemsAsync(items, targetFolder.FullPath);
            return;
        }

        if (!payload.FromRemote)
        {
            // Same-pane drop on empty space: move to current directory if we navigated here
            var sourcePath = items.Count > 0
                ? Path.GetDirectoryName(items[0].FullPath) ?? _localPath
                : _localPath;
            if (_localPath != sourcePath)
                await MoveLocalItemsAsync(items, _localPath);
            return;
        }

        await DownloadItemsAsync(items);
    }

    async void OnRemoteDrop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDataFormat)) return;
        var payload = (DragPayload)e.Data.GetData(DragDataFormat);
        var items   = payload.Items.ToList();

        // Drop onto a subfolder in this pane → move into it
        var targetFolder = GetEntryAt(RemoteList, e.GetPosition(RemoteList));
        if (payload.FromRemote && targetFolder is { IsDirectory: true })
        {
            await MoveRemoteItemsAsync(items, targetFolder.FullPath);
            return;
        }

        if (payload.FromRemote)
        {
            // Same-pane drop on empty space: move to current directory if we navigated here
            var sourcePath = items.Count > 0 ? GetFtpParent(items[0].FullPath) : _remotePath;
            if (_remotePath != sourcePath)
                await MoveRemoteItemsAsync(items, _remotePath);
            return;
        }

        await UploadItemsAsync(items);
    }

    async Task MoveLocalItemsAsync(IList<FileEntry> items, string destFolder)
    {
        SetBusy(true, $"Moving {items.Count} file(s)…");
        int done = 0;
        foreach (var item in items)
        {
            var dest = Path.Combine(destFolder, item.Name);
            try { File.Move(item.FullPath, dest, overwrite: true); done++; }
            catch (Exception ex) { SetStatus($"Failed {item.Name}: {ex.Message}"); }
        }
        RefreshLocal();
        SetStatus($"Moved {done}/{items.Count} file(s) → {destFolder}");
        SetBusy(false);
    }

    async Task MoveRemoteItemsAsync(IList<FileEntry> items, string destFolder)
    {
        if (!await EnsureConnectedAsync()) return;
        SetBusy(true, $"Moving {items.Count} file(s)…");
        int done = 0;
        foreach (var item in items)
        {
            var dest = $"{destFolder.TrimEnd('/')}/{item.Name}";
            try { await _ftp!.Rename(item.FullPath, dest); done++; }
            catch (Exception ex) { SetStatus($"Failed {item.Name}: {ex.Message}"); }
        }
        await RefreshRemoteAsync();
        SetStatus($"Moved {done}/{items.Count} file(s) → {destFolder}");
        SetBusy(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    async Task<bool> EnsureConnectedAsync()
    {
        if (_ftp == null) return false;
        if (!_ftp.IsConnected)
        {
            try
            {
                SetStatus("Reconnecting…");
                await Task.Run(() => _ftp.Connect(CancellationToken.None));
            }
            catch (Exception ex) { SetStatus($"FTP reconnect failed: {ex.Message}"); return false; }
        }
        return true;
    }

    static string GetFtpParent(string path)
    {
        var clean = path.TrimEnd('/');
        var slash = clean.LastIndexOf('/');
        return slash <= 0 ? "/" : clean[..slash];
    }

    ConflictResult AskConflict(string fileName)
    {
        var dlg = new ConflictDialog(fileName, this);
        dlg.ShowDialog();
        return new ConflictResult(dlg.Action, dlg.ResultName, dlg.ApplyToAll);
    }

    static FileEntry? GetEntryAt(ListView lv, Point pt)
    {
        var hit = lv.InputHitTest(pt) as DependencyObject;
        while (hit != null)
        {
            if (hit is ListViewItem lvi) return lvi.Content as FileEntry;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    // ── Right-click context menus ─────────────────────────────────────────────
    void PrepareContextMenu(ListView lv, bool isRemote, MouseButtonEventArgs e)
    {
        var entry = GetEntryAt(lv, e.GetPosition(lv));
        // Right-click on an unselected item: select just that item
        if (entry != null && !lv.SelectedItems.Contains(entry))
            lv.SelectedItem = entry;
        lv.ContextMenu = BuildContextMenu(lv, isRemote, entry);
    }

    ContextMenu BuildContextMenu(ListView lv, bool isRemote, FileEntry? entry)
    {
        bool onFolder     = entry is { IsDirectory: true };
        bool hasFiles     = lv.SelectedItems.Cast<FileEntry>().Any(f => !f.IsDirectory);
        bool hasSelection = lv.SelectedItems.Count > 0;
        bool isSingle     = lv.SelectedItems.Count == 1;

        var cm = new ContextMenu();

        // First item: "Open" for folders, transfer command otherwise
        if (onFolder)
        {
            cm.Items.Add(MakeItem("Open", true, async (_, _) =>
            {
                if (isRemote) { _remotePath = entry!.FullPath; await RefreshRemoteAsync(); }
                else          { _localPath  = entry!.FullPath; RefreshLocal(); }
            }));
        }
        else
        {
            cm.Items.Add(MakeItem(
                isRemote ? "← Send to PC" : "→ Send to Kronos",
                hasFiles,
                isRemote ? (RoutedEventHandler)OnDownload : OnUpload));
        }

        cm.Items.Add(MakeItem("Cut",   hasSelection, (_, _) => DoCut(lv, isRemote)));
        cm.Items.Add(MakeItem("Copy",  hasSelection, (_, _) => DoCopy(lv, isRemote)));
        cm.Items.Add(MakeItem("Paste", _clipboard != null && !_busy,
                              async (_, _) => await DoPasteAsync(isRemote)));
        cm.Items.Add(new Separator());
        cm.Items.Add(MakeItem("Rename", isSingle && entry != null,
                     isRemote ? (RoutedEventHandler)OnRemoteRename : OnLocalRename));
        cm.Items.Add(MakeItem("Delete", hasSelection,
                     isRemote ? (RoutedEventHandler)OnRemoteDelete : OnLocalDelete));
        cm.Items.Add(new Separator());
        cm.Items.Add(MakeItem("New Folder", !_busy,
                     isRemote ? (RoutedEventHandler)OnRemoteNewFolder : OnLocalNewFolder));
        cm.Items.Add(MakeItem("Refresh",    !_busy,
                     isRemote ? (RoutedEventHandler)OnRemoteRefresh   : OnLocalRefresh));

        return cm;
    }

    static MenuItem MakeItem(string header, bool enabled, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += onClick;
        return item;
    }

    // ── Clipboard operations ──────────────────────────────────────────────────
    void DoCut(ListView lv, bool isRemote)
    {
        var items = lv.SelectedItems.Cast<FileEntry>().ToList(); // files + dirs
        if (items.Count == 0) return;
        _clipboard = new ClipboardPayload(IsCut: true, FromRemote: isRemote, Items: items);
        SetStatus($"Cut {items.Count} item(s) — paste to move.");
    }

    void DoCopy(ListView lv, bool isRemote)
    {
        var items = lv.SelectedItems.Cast<FileEntry>().ToList(); // files + dirs
        if (items.Count == 0) return;
        _clipboard = new ClipboardPayload(IsCut: false, FromRemote: isRemote, Items: items);
        SetStatus($"Copied {items.Count} item(s) — paste to copy.");
    }

    async Task DoPasteAsync(bool toRemote)
    {
        if (_clipboard == null) return;
        var cb    = _clipboard;
        var items = cb.Items.ToList();

        if (!cb.FromRemote && !toRemote)
        {
            // Local → Local
            if (cb.IsCut) await MoveLocalItemsAsync(items, _localPath);
            else          await CopyLocalItemsAsync(items, _localPath);
            if (cb.IsCut) _clipboard = null;
        }
        else if (cb.FromRemote && toRemote)
        {
            // Remote → Remote
            if (cb.IsCut) await MoveRemoteItemsAsync(items, _remotePath);
            else          await CopyRemoteItemsAsync(items, _remotePath);
            if (cb.IsCut) _clipboard = null;
        }
        else if (!cb.FromRemote)
        {
            // Local → Remote
            var files = items.Where(f => !f.IsDirectory).ToList();
            var dirs  = items.Where(f =>  f.IsDirectory).ToList();
            if (files.Count > 0) await UploadItemsAsync(files);
            if (dirs.Count  > 0 && await EnsureConnectedAsync())
            {
                foreach (var dir in dirs)
                {
                    SetStatus($"Uploading folder {dir.Name}…");
                    try { await _ftp!.UploadDirectory(dir.FullPath, $"{_remotePath.TrimEnd('/')}/{dir.Name}", FtpFolderSyncMode.Update); }
                    catch (Exception ex) { SetStatus($"Failed {dir.Name}: {ex.Message}"); }
                }
                await RefreshRemoteAsync();
            }
            if (cb.IsCut)
            {
                foreach (var f in files) try { File.Delete(f.FullPath); }            catch { }
                foreach (var d in dirs)  try { Directory.Delete(d.FullPath, true); } catch { }
                RefreshLocal();
                _clipboard = null;
            }
        }
        else
        {
            // Remote → Local
            var files = items.Where(f => !f.IsDirectory).ToList();
            var dirs  = items.Where(f =>  f.IsDirectory).ToList();
            if (files.Count > 0) await DownloadItemsAsync(files);
            if (dirs.Count  > 0 && await EnsureConnectedAsync())
            {
                foreach (var dir in dirs)
                {
                    SetStatus($"Downloading folder {dir.Name}…");
                    try { await _ftp!.DownloadDirectory(Path.Combine(_localPath, dir.Name), dir.FullPath, FtpFolderSyncMode.Update); }
                    catch (Exception ex) { SetStatus($"Failed {dir.Name}: {ex.Message}"); }
                }
                RefreshLocal();
            }
            if (cb.IsCut)
            {
                if (await EnsureConnectedAsync())
                {
                    foreach (var f in files) try { await _ftp!.DeleteFile(f.FullPath); }      catch { }
                    foreach (var d in dirs)  try { await _ftp!.DeleteDirectory(d.FullPath); } catch { }
                    await RefreshRemoteAsync();
                }
                _clipboard = null;
            }
        }
    }

    Task CopyLocalItemsAsync(IList<FileEntry> items, string destFolder)
    {
        SetBusy(true, $"Copying {items.Count} item(s)…");
        int done = 0;
        ConflictResult? remembered = null;
        ConflictResult Resolve(string fn)
        {
            if (remembered != null)
                return remembered.Action == ConflictAction.Rename
                    ? remembered with { Name = ConflictDialog.SuggestName(fn) }
                    : remembered;
            var r = AskConflict(fn);
            if (r.ApplyToAll) remembered = r;
            return r;
        }
        foreach (var item in items)
        {
            var fileName = item.Name;
            var dest     = Path.Combine(destFolder, fileName);
            bool exists  = item.IsDirectory ? Directory.Exists(dest) : File.Exists(dest);
            if (exists)
            {
                var r = Resolve(fileName);
                if (r.Action == ConflictAction.Cancel) break;
                if (r.Action == ConflictAction.Skip)   continue;
                if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = Path.Combine(destFolder, fileName); }
            }
            try
            {
                if (item.IsDirectory)
                    CopyDirectoryRecursive(item.FullPath, dest);
                else
                    File.Copy(item.FullPath, dest, overwrite: true);
                done++;
            }
            catch (Exception ex) { SetStatus($"Failed {item.Name}: {ex.Message}"); }
        }
        RefreshLocal();
        SetStatus($"Copied {done}/{items.Count} item(s) → {destFolder}");
        SetBusy(false);
        return Task.CompletedTask;
    }

    static void CopyDirectoryRecursive(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectoryRecursive(d, Path.Combine(dest, new DirectoryInfo(d).Name));
    }

    async Task CopyRemoteItemsAsync(IList<FileEntry> items, string destFolder)
    {
        if (!await EnsureConnectedAsync()) return;
        SetBusy(true, $"Copying {items.Count} item(s)…");
        int done    = 0;
        var tempDir = Path.Combine(Path.GetTempPath(), "KronosCopy_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        ConflictResult? remembered = null;
        ConflictResult Resolve(string fn)
        {
            if (remembered != null)
                return remembered.Action == ConflictAction.Rename
                    ? remembered with { Name = ConflictDialog.SuggestName(fn) }
                    : remembered;
            var r = AskConflict(fn);
            if (r.ApplyToAll) remembered = r;
            return r;
        }
        try
        {
            foreach (var item in items)
            {
                var fileName = item.Name;
                var dest     = $"{destFolder.TrimEnd('/')}/{fileName}";
                bool exists  = item.IsDirectory
                    ? await _ftp!.DirectoryExists(dest)
                    : await _ftp!.FileExists(dest);
                if (exists)
                {
                    var r = Resolve(fileName);
                    if (r.Action == ConflictAction.Cancel) break;
                    if (r.Action == ConflictAction.Skip)   continue;
                    if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = $"{destFolder.TrimEnd('/')}/{fileName}"; }
                }
                try
                {
                    if (item.IsDirectory)
                    {
                        var localDir = Path.Combine(tempDir, item.Name);
                        SetStatus($"Copying folder {item.Name}…");
                        await _ftp!.DownloadDirectory(localDir, item.FullPath, FtpFolderSyncMode.Update);
                        await _ftp!.UploadDirectory(localDir, dest, FtpFolderSyncMode.Update);
                    }
                    else
                    {
                        var tempPath = Path.Combine(tempDir, item.Name);
                        await _ftp!.DownloadFile(tempPath, item.FullPath, FtpLocalExists.Overwrite);
                        await _ftp!.UploadFile(tempPath, dest, FtpRemoteExists.Overwrite);
                    }
                    done++;
                }
                catch (Exception ex) { SetStatus($"Failed {item.Name}: {ex.Message}"); }
            }
        }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
        await RefreshRemoteAsync();
        SetStatus($"Copied {done}/{items.Count} item(s) → {destFolder}");
        SetBusy(false);
    }

    // ── Local drive selector ──────────────────────────────────────────────────
    void PopulateLocalDrives()
    {
        _suppressDriveChange = true;
        LocalDriveCombo.Items.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string prefix = drive.DriveType switch
            {
                DriveType.Removable => "💾 ",
                DriveType.CDRom     => "💿 ",
                DriveType.Network   => "🌐 ",
                _                   => "💽 "
            };
            string name = drive.Name.TrimEnd('\\');
            string label = "";
            try { if (drive.IsReady) label = drive.VolumeLabel; } catch { }
            string display = string.IsNullOrWhiteSpace(label)
                ? $"{prefix}{name}"
                : $"{prefix}{name}  {label}";
            LocalDriveCombo.Items.Add(new DriveItem(drive.Name, display));
        }
        SyncDriveComboCore();
        _suppressDriveChange = false;
    }

    void SyncDriveCombo()
    {
        if (LocalDriveCombo.Items.Count == 0) return;
        _suppressDriveChange = true;
        SyncDriveComboCore();
        _suppressDriveChange = false;
    }

    void SyncDriveComboCore()
    {
        var root = Path.GetPathRoot(_localPath);
        foreach (DriveItem item in LocalDriveCombo.Items)
        {
            if (string.Equals(item.RootPath, root, StringComparison.OrdinalIgnoreCase))
            { LocalDriveCombo.SelectedItem = item; return; }
        }
    }

    void OnLocalDriveDropDownOpened(object s, EventArgs e)
    {
        PopulateLocalDrives();
        _suppressDriveChange = true;
        LocalDriveCombo.SelectedIndex = -1;
        _suppressDriveChange = false;
    }

    void OnLocalDriveChanged(object s, SelectionChangedEventArgs e)
    {
        if (_suppressDriveChange) return;
        if (LocalDriveCombo.SelectedItem is not DriveItem drive) return;
        _localPath = drive.RootPath;
        RefreshLocal();
    }

    void SetStatus(string msg) => StatusText.Text = msg;

    void SetBusy(bool busy, string msg = "")
    {
        _busy = busy;
        BtnUpload.IsEnabled          = !busy;
        BtnLocalNewFolder.IsEnabled  = !busy;
        BtnLocalDelete.IsEnabled     = !busy;
        BtnLocalRename.IsEnabled     = !busy;
        BtnLocalRefresh.IsEnabled    = !busy;
        BtnDownload.IsEnabled        = !busy;
        BtnRemoteNewFolder.IsEnabled = !busy;
        BtnRemoteDelete.IsEnabled    = !busy;
        BtnRemoteRename.IsEnabled    = !busy;
        BtnRemoteRefresh.IsEnabled   = !busy;
        TransferProgress.Visibility  = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) { TransferProgress.Value = 0; SetStatus(msg); }
    }

    string? PromptInput(string prompt, string initial = "")
    {
        var dlg = new PromptDialog(prompt, initial) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ── Column sort ───────────────────────────────────────────────────────────
    void OnLocalColumnHeaderClick(object s, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h ||
            h.Column == null || h.Role != GridViewColumnHeaderRole.Normal) return;
        var col = MapColumn(h.Column, isRemote: false);
        if (col == null) return;
        if (col == _localSortCol) _localSortAsc = !_localSortAsc;
        else { _localSortCol = col.Value; _localSortAsc = true; }
        ApplySort(_localItems, _localSortCol, _localSortAsc);
        UpdateLocalHeaders();
    }

    void OnRemoteColumnHeaderClick(object s, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h ||
            h.Column == null || h.Role != GridViewColumnHeaderRole.Normal) return;
        var col = MapColumn(h.Column, isRemote: true);
        if (col == null) return;
        if (col == _remoteSortCol) _remoteSortAsc = !_remoteSortAsc;
        else { _remoteSortCol = col.Value; _remoteSortAsc = true; }
        ApplySort(_remoteItems, _remoteSortCol, _remoteSortAsc);
        UpdateRemoteHeaders();
    }

    SortColumn? MapColumn(GridViewColumn col, bool isRemote)
        => isRemote
            ? (col == _remoteNameCol ? SortColumn.Name
             : col == _remoteSizeCol ? SortColumn.Size
             : col == _remoteDateCol ? SortColumn.Modified
             : (SortColumn?)null)
            : (col == _localNameCol ? SortColumn.Name
             : col == _localSizeCol ? SortColumn.Size
             : col == _localDateCol ? SortColumn.Modified
             : (SortColumn?)null);

    static IEnumerable<FileEntry> SortEntries(IEnumerable<FileEntry> src, SortColumn col, bool asc)
        => col switch
        {
            SortColumn.Size => asc
                ? src.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Bytes)
                : src.OrderBy(e => e.IsDirectory ? 0 : 1).ThenByDescending(e => e.Bytes),
            SortColumn.Modified => asc
                ? src.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Modified)
                : src.OrderBy(e => e.IsDirectory ? 0 : 1).ThenByDescending(e => e.Modified),
            _ => asc
                ? src.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : src.OrderBy(e => e.IsDirectory ? 0 : 1).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };

    void ApplySort(ObservableCollection<FileEntry> items, SortColumn col, bool asc)
    {
        var sorted = SortEntries(items, col, asc).ToList();
        items.Clear();
        foreach (var e in sorted) items.Add(e);
    }

    void UpdateLocalHeaders()
    {
        _localNameCol.Header = "Name (Local)" + Ind(_localSortCol,  _localSortAsc,  SortColumn.Name);
        _localSizeCol.Header = "Size"         + Ind(_localSortCol,  _localSortAsc,  SortColumn.Size);
        _localDateCol.Header = "Modified"     + Ind(_localSortCol,  _localSortAsc,  SortColumn.Modified);
    }

    void UpdateRemoteHeaders()
    {
        _remoteNameCol.Header = "Name (Kronos)" + Ind(_remoteSortCol, _remoteSortAsc, SortColumn.Name);
        _remoteSizeCol.Header = "Size"          + Ind(_remoteSortCol, _remoteSortAsc, SortColumn.Size);
        _remoteDateCol.Header = "Modified"      + Ind(_remoteSortCol, _remoteSortAsc, SortColumn.Modified);
    }

    static string Ind(SortColumn active, bool asc, SortColumn target)
        => active == target ? (asc ? " ▲" : " ▼") : "";

    // ── Drag-scroll ───────────────────────────────────────────────────────────
    static ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv) return sv;
            var found = GetScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    void OnDragScrollTick(object? s, EventArgs e)
        => _dragScrollViewer?.ScrollToVerticalOffset(
               _dragScrollViewer.VerticalOffset + _dragScrollDelta);

    void StopDragScroll()
    {
        _dragScrollTimer.Stop();
        _dragScrollViewer = null;
        _dragScrollDelta  = 0;
    }
}
