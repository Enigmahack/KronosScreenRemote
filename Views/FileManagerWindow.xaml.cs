using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluentFTP;
// Note: System.Windows.Media is kept for SolidColorBrush used in RubberBandAdorner

namespace KronosScreenRemote;

public partial class FileManagerWindow : ThemedWindow
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

    // Per-pane state (Local vs Kronos): item list, sortable column refs, and current sort.
    // Lets the identical sort/header/column logic be written once, parameterized by pane. The
    // divergent I/O (synchronous local FS vs async FTP) deliberately stays in separate methods
    // that read this shared state.
    sealed class Pane(bool isRemote, string nameHeader, string dir)
    {
        public readonly bool   IsRemote   = isRemote;
        public readonly string NameHeader = nameHeader;
        public readonly ObservableCollection<FileEntry> Items = new();

        public string        Dir = dir;
        public ScrollViewer? ScrollViewer;                // cached in OnLoaded for drag-scroll

        public GridViewColumn NameCol = null!;
        public GridViewColumn SizeCol = null!;
        public GridViewColumn DateCol = null!;

        public SortColumn SortCol = SortColumn.Name;
        public bool       SortAsc = true;
    }

    // ── Conflict dialog (Rename / Overwrite / Skip / Cancel) ─────────────────
    sealed class ConflictDialog : ThemedWindow
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

            // Cancel goes far-left so the app's convention holds (the bottom-right slot is never
            // a cancel/escape). Rename/Overwrite/Skip are all "proceed" variants - no single
            // affirmative - so they keep their established left-to-right order.
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            btnRow.Children.Add(Btn("Cancel",    ConflictAction.Cancel));
            btnRow.Children.Add(Btn("Rename",    ConflictAction.Rename));
            btnRow.Children.Add(Btn("Overwrite", ConflictAction.Overwrite));
            btnRow.Children.Add(Btn("Skip",      ConflictAction.Skip));

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

    readonly Pane _local  = new(isRemote: false, nameHeader: "Name (Local)", dir: ResolveStartFolder());
    readonly Pane _remote = new(isRemote: true,  nameHeader: "Name (Kronos)", dir: "/");

    bool _busy;
    bool _suppressDriveChange;

    // FluentFTP's control connection cannot run two commands concurrently.  Every FTP-initiating
    // action goes through this gate so operations run one at a time.  RULE: only the outermost
    // entry points (async void handlers, keyboard/menu worker calls) acquire it via RunExclusive;
    // the async Task worker methods (RefreshRemoteAsync, Upload/Download/Move/Copy, DoPasteAsync,
    // EnsureConnectedAsync) must NEVER acquire it, or they'd deadlock when called from a worker.
    readonly SemaphoreSlim _ftpGate = new(1, 1);

    ClipboardPayload? _clipboard;

    // The synthetic "move up" row every listing gets (unless already at a drive/FTP root) - a
    // real FileEntry (IsDirectory=true, FullPath=parent), not a separate model, so every
    // navigate-into-a-folder code path already handles it for free. RealItems() is what keeps it
    // OUT of anything that acts destructively on a selection (Delete/Rename/Cut/Copy/transfer/
    // drag-source) - navigation itself needs no such filter (see RefreshRemoteAsync's comment).
    const string ParentEntryName = "..";

    static List<FileEntry> RealItems(IEnumerable<FileEntry> items) =>
        items.Where(f => f.Name != ParentEntryName).ToList();

    const string DragDataFormat = "KronosScreenRemote.FileEntries";
    ListView?    _dragSource;
    Point        _dragStart;
    FileEntry?   _deferredSelectEntry; // item to solo-select on mouseup when no drag occurred

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


    // The local pane's starting folder: unset (never configured via right-click "Set Default
    // Start Folder") keeps today's Desktop default; set but no longer reachable (moved, deleted,
    // a removable/network drive not currently present) falls back to the system drive's root,
    // per the explicit ask, rather than silently reverting to Desktop.
    static string ResolveStartFolder()
    {
        var saved = Storage.LoadSettings().FileManagerDefaultLocalFolder;
        if (string.IsNullOrWhiteSpace(saved)) return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return Directory.Exists(saved) ? saved : "C:\\";
    }

    // ── Constructor ───────────────────────────────────────────────────────────
    public FileManagerWindow(string host, int ftpPort, string user, string pass)
    {
        _host    = host;
        _ftpPort = ftpPort;
        _user    = user;
        _pass    = pass;
        InitializeComponent();
        LocalList.ItemsSource  = _local.Items;
        RemoteList.ItemsSource = _remote.Items;

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

        // Column sort - cache GridViewColumn refs and stamp initial ▲ on Name header
        var lg = (GridView)LocalList.View;
        var rg = (GridView)RemoteList.View;
        _local.NameCol  = lg.Columns[0];
        _local.SizeCol  = lg.Columns[1];
        _local.DateCol  = lg.Columns[2];
        _remote.NameCol = rg.Columns[0];
        _remote.SizeCol = rg.Columns[1];
        _remote.DateCol = rg.Columns[2];
        UpdateHeaders(_local);
        UpdateHeaders(_remote);
        LocalList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler((s, e) => OnColumnHeaderClick(_local, e)));
        RemoteList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler((s, e) => OnColumnHeaderClick(_remote, e)));

        _dragScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _dragScrollTimer.Tick    += OnDragScrollTick;

        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    async void OnLoaded(object s, RoutedEventArgs e)
    {
        _local.ScrollViewer  = GetScrollViewer(LocalList);
        _remote.ScrollViewer = GetScrollViewer(RemoteList);
        PopulateLocalDrives();
        _ftp = KronosFtpSession.CreateClient(_host, _ftpPort, _user, _pass);
        try
        {
            SetStatus(AppMessages.FileManager.Connecting);
            await RunExclusive(async () =>
            {
                await Task.Run(() => _ftp.Connect(CancellationToken.None));
                SetStatus(AppMessages.FileManager.Connected);
                await RefreshBothAsync();
            });
        }
        catch (Exception ex)
        {
            SetStatus(AppMessages.FileManager.ConnectFailed(ex.Message));
        }
    }

    void OnClosing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        // Title-bar close and Alt+F4 bypass every button's IsEnabled=false, so without this
        // check a close mid-transfer would still run the cleanup below - nulling and
        // disconnecting _ftp out from under an in-flight operation that never coordinated
        // with it (same failure mode SampleRemoteBrowserDialog's BlockCloseWhileBusy guards
        // against). Checked FIRST and returns immediately: a plain CLR event still invokes
        // every subscriber regardless of what an earlier one sets on a shared CancelEventArgs,
        // so the guard has to live inside the one handler that does the actual cleanup rather
        // than in a second Closing subscriber.
        if (_busy) { e.Cancel = true; return; }

        // Break the Owner link before closing so WPF doesn't minimize the parent
        // when this window had focus (known WPF owner-activation bug).
        Owner = null;

        // Closing mid file-drag: both timers would otherwise keep ticking and root
        // this window via the dispatcher.
        StopDragScroll();
        CancelDwell();

        // Hand the client off to a background thread so the UI thread isn't blocked.
        // Send QUIT first so BusyBox ftpd cleanly removes the session - without it the
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
        await NavigateRemoteAsync(item.FullPath);
    }

    void OnLocalDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (LocalList.SelectedItem is not FileEntry item || !item.IsDirectory) return;
        _local.Dir = item.FullPath;
        RefreshLocal();
    }

    async void OnRemoteUp(object s, RoutedEventArgs e)
    {
        var parent = GetFtpParent(_remote.Dir);
        if (parent == _remote.Dir) return;
        await NavigateRemoteAsync(parent);
    }

    void OnLocalUp(object s, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_local.Dir)?.FullName;
        if (parent == null) return;
        _local.Dir = parent;
        RefreshLocal();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────
    async void OnRemoteRefresh(object s, RoutedEventArgs e) => await RunExclusive(() => RefreshRemoteAsync());
    void       OnLocalRefresh (object s, RoutedEventArgs e) => RefreshLocal();

    async Task RefreshBothAsync() { await RefreshRemoteAsync(); RefreshLocal(); }

    async Task RunExclusive(Func<Task> op)
    {
        await _ftpGate.WaitAsync();
        try { await op(); }
        finally { _ftpGate.Release(); }
    }

    // Owns SetBusy(true)/SetBusy(false) around a transfer/move/copy body via try/finally, so a
    // throw anywhere inside it (including the post-loop refresh call) can never leave the window
    // stuck busy with every button disabled - the failure mode BlockCloseWhileBusy below would
    // otherwise turn into an unclosable window. `op` does its own operation-specific status/
    // refresh calls; this only guarantees the busy flag itself always clears.
    async Task<T> RunBusyAsync<T>(string message, Func<Task<T>> op)
    {
        SetBusy(true, message);
        try { return await op(); }
        finally { SetBusy(false); }
    }

    async Task RunBusyAsync(string message, Func<Task> op)
    {
        SetBusy(true, message);
        try { await op(); }
        finally { SetBusy(false); }
    }

    // Navigate the remote pane to a folder, rolling the path back if the listing fails so the
    // path box and the shown contents never disagree. The path swap happens INSIDE the gate
    // so two rapid navigations can't clobber each other's target/rollback.
    async Task NavigateRemoteAsync(string path)
    {
        await RunExclusive(async () =>
        {
            var prev = _remote.Dir;
            _remote.Dir = path;
            if (!await RefreshRemoteAsync()) _remote.Dir = prev;
        });
    }

    async Task<bool> RefreshRemoteAsync()
    {
        if (!await EnsureConnectedAsync()) return false;
        SetStatus(AppMessages.FileManager.Loading(_remote.Dir));
        try
        {
            var listing = await _ftp!.GetListing(_remote.Dir);
            var entries = listing
                .Select(i => new FileEntry(i.Name, i.FullName,
                    i.Type == FtpObjectType.Directory, i.Size, i.Modified))
                .ToList();
            _remote.Items.Clear();
            foreach (var entry in entries) _remote.Items.Add(entry);
            // ".." - a real directory entry pointing at the parent, not a special case: every
            // existing navigate-into-a-folder path (double-click, Enter, Open, drag-hover-dwell,
            // drop-onto) already just works once IsDirectory/FullPath are set this way. Only
            // multi-select actions that ACT on a selection (Delete/Rename/Cut/Copy/transfer/drag-
            // source) need to filter it back out - see ParentEntryName's own callers.
            var parent = GetFtpParent(_remote.Dir);
            if (parent != _remote.Dir) _remote.Items.Add(new FileEntry(ParentEntryName, parent, true, 0, default));
            ApplySort(_remote.Items, _remote.SortCol, _remote.SortAsc);
            RemotePathBox.Text = _remote.Dir;   // reflect only after a successful listing
            SetStatus(AppMessages.FileManager.ItemsIn(entries.Count, _remote.Dir));
            return true;
        }
        catch (Exception ex) { SetStatus(AppMessages.FileManager.ErrorListingRemote(ex.Message)); return false; }
    }

    void RefreshLocal()
    {
        LocalPathBox.Text = _local.Dir;
        SyncDriveCombo();
        try
        {
            _local.Items.Clear();
            int count = 0;
            foreach (var d in Directory.GetDirectories(_local.Dir).Select(p => new DirectoryInfo(p)))
                { _local.Items.Add(new FileEntry(d.Name, d.FullName, true, 0, d.LastWriteTime)); count++; }
            foreach (var f in Directory.GetFiles(_local.Dir).Select(p => new FileInfo(p)))
                { _local.Items.Add(new FileEntry(f.Name, f.FullName, false, f.Length, f.LastWriteTime)); count++; }
            // ".." - see RefreshRemoteAsync's own comment on why this is a plain directory entry.
            if (Directory.GetParent(_local.Dir)?.FullName is { } parent)
                _local.Items.Add(new FileEntry(ParentEntryName, parent, true, 0, default));
            ApplySort(_local.Items, _local.SortCol, _local.SortAsc);
            SetStatus(AppMessages.FileManager.ItemsIn(count, _local.Dir));
        }
        catch (Exception ex) { SetStatus(AppMessages.FileManager.ErrorListingLocal(ex.Message)); }
    }

    // ── Upload (local → Kronos) ───────────────────────────────────────────────
    async void OnUpload(object s, RoutedEventArgs e)
    {
        var items = RealItems(LocalList.SelectedItems.Cast<FileEntry>());
        if (items.Count == 0) { SetStatus(AppMessages.FileManager.SelectLocalFilesToUpload); return; }
        var files = items.Where(f => !f.IsDirectory).ToList();
        var dirs  = items.Where(f =>  f.IsDirectory).ToList();
        await RunExclusive(async () =>
        {
            if (files.Count > 0) await UploadItemsAsync(files);
            if (dirs.Count  > 0) await UploadFoldersAsync(dirs);
        });
    }

    // Returns the items whose upload verifiably succeeded, so a cut/move can delete only those
    // sources.  FluentFTP's UploadFile can report Failed/Skipped WITHOUT throwing, so success is
    // gated on FtpStatus.Success - never on mere absence of an exception (that would still delete
    // the source of a silently-failed upload).
    async Task<List<FileEntry>> UploadItemsAsync(IList<FileEntry> items)
    {
        var moved = new List<FileEntry>();
        if (!await EnsureConnectedAsync()) return moved;
        var remoteNames = _remote.Items.Where(f => !f.IsDirectory)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await RunBusyAsync(AppMessages.FileManager.Uploading(items.Count), async () =>
        {
            int done = 0;
            var resolve = MakeConflictResolver();
            foreach (var (local, idx) in items.Select((x, i) => (x, i)))
            {
                var fileName = Path.GetFileName(local.FullPath);
                var dest     = $"{_remote.Dir.TrimEnd('/')}/{fileName}";
                if (remoteNames.Contains(fileName))
                {
                    var r = resolve(fileName);
                    if (r.Action == ConflictAction.Cancel) break;
                    if (r.Action == ConflictAction.Skip)   continue;
                    if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = $"{_remote.Dir.TrimEnd('/')}/{fileName}"; }
                }
                // Upload to a unique sibling path first, so a disconnect mid-transfer can never
                // truncate a previously-valid remote file - the real dest is only replaced (via
                // Rename, already proven against the Kronos's BusyBox ftpd by MoveRemoteItemsAsync)
                // once the whole upload has verifiably succeeded.
                var part = $"{dest}.{Guid.NewGuid().ToString("N")[..8]}.part";
                try
                {
                    var progress = new Progress<FtpProgress>(p => Dispatcher.InvokeAsync(() =>
                    {
                        TransferProgress.Value = p.Progress;
                        SetStatus(AppMessages.FileManager.ItemProgress(idx + 1, items.Count, local.Name, p.Progress));
                    }));
                    var st = await _ftp!.UploadFile(local.FullPath, part, FtpRemoteExists.Overwrite,
                                                    createRemoteDir: true, progress: progress);
                    if (st == FtpStatus.Success)
                    {
                        if (await _ftp!.FileExists(dest)) await _ftp!.DeleteFile(dest);
                        await _ftp!.RenameGuardedAsync(part, dest);
                        done++; moved.Add(local);
                    }
                    else
                    {
                        SetStatus(AppMessages.FileManager.UploadIncomplete(local.Name, st));
                        try { await _ftp!.DeleteFile(part); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus(AppMessages.FileManager.FailedItem(local.Name, ex.Message));
                    try { await _ftp!.DeleteFile(part); } catch { }
                }
            }
            await RefreshRemoteAsync();
            SetStatus(AppMessages.FileManager.Uploaded(done, items.Count, _remote.Dir));
            return moved;
        });
    }

    // ── Download (Kronos → local) ─────────────────────────────────────────────
    async void OnDownload(object s, RoutedEventArgs e)
    {
        var items = RealItems(RemoteList.SelectedItems.Cast<FileEntry>());
        if (items.Count == 0) { SetStatus(AppMessages.FileManager.SelectKronosFilesToDownload); return; }
        var files = items.Where(f => !f.IsDirectory).ToList();
        var dirs  = items.Where(f =>  f.IsDirectory).ToList();
        await RunExclusive(async () =>
        {
            if (files.Count > 0) await DownloadItemsAsync(files);
            if (dirs.Count  > 0) await DownloadFoldersAsync(dirs);
        });
    }

    // Returns the items whose download verifiably succeeded (FtpStatus.Success only), so a
    // remote→local cut deletes only those remote sources.  See UploadItemsAsync for the rationale.
    async Task<List<FileEntry>> DownloadItemsAsync(IList<FileEntry> items)
    {
        var moved = new List<FileEntry>();
        if (!await EnsureConnectedAsync()) return moved;
        return await RunBusyAsync(AppMessages.FileManager.Downloading(items.Count), async () =>
        {
            int done = 0;
            var resolve = MakeConflictResolver();
            foreach (var (remote, idx) in items.Select((x, i) => (x, i)))
            {
                var fileName = Path.GetFileName(remote.FullPath);
                var dest     = Path.Combine(_local.Dir, fileName);
                if (File.Exists(dest))
                {
                    var r = resolve(fileName);
                    if (r.Action == ConflictAction.Cancel) break;
                    if (r.Action == ConflictAction.Skip)   continue;
                    if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = Path.Combine(_local.Dir, fileName); }
                }
                // Download to a unique sibling path first - a disconnect mid-transfer must not
                // truncate a previously-valid local file. Promote via File.Move only once the
                // whole download has verifiably succeeded.
                var part = $"{dest}.{Guid.NewGuid().ToString("N")[..8]}.part";
                try
                {
                    var progress = new Progress<FtpProgress>(p => Dispatcher.InvokeAsync(() =>
                    {
                        TransferProgress.Value = p.Progress;
                        SetStatus(AppMessages.FileManager.ItemProgress(idx + 1, items.Count, remote.Name, p.Progress));
                    }));
                    var st = await _ftp!.DownloadFile(part, remote.FullPath, FtpLocalExists.Overwrite,
                                                      FtpVerify.None, progress);
                    if (st == FtpStatus.Success)
                    {
                        File.Move(part, dest, overwrite: true);
                        done++; moved.Add(remote);
                    }
                    else
                    {
                        SetStatus(AppMessages.FileManager.DownloadIncomplete(remote.Name, st));
                        try { if (File.Exists(part)) File.Delete(part); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus(AppMessages.FileManager.FailedItem(remote.Name, ex.Message));
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                }
            }
            RefreshLocal();
            SetStatus(AppMessages.FileManager.Downloaded(done, items.Count, _local.Dir));
            return moved;
        });
    }

    // ── Folder transfer (local ↔ Kronos, recursive) ───────────────────────────
    // FluentFTP's UploadDirectory/DownloadDirectory walk the tree, create subfolders,
    // and transfer every file themselves - no need to hand-roll recursion here.
    // Returns the folders whose transfer verifiably succeeded (no failed files), so a
    // cut/move can delete only those sources - same contract as UploadItemsAsync.
    async Task<List<FileEntry>> UploadFoldersAsync(IList<FileEntry> dirs)
    {
        var moved = new List<FileEntry>();
        if (dirs.Count == 0 || !await EnsureConnectedAsync()) return moved;
        return await RunBusyAsync(AppMessages.FileManager.Uploading(dirs.Count), async () =>
        {
            foreach (var dir in dirs)
            {
                SetStatus(AppMessages.FileManager.UploadingFolder(dir.Name));
                try
                {
                    var dest = $"{_remote.Dir.TrimEnd('/')}/{dir.Name}";
                    var res  = await _ftp!.UploadDirectory(dir.FullPath, dest, FtpFolderSyncMode.Update);
                    if (res.All(r => r.IsSuccess || r.IsSkipped)) moved.Add(dir);
                    else SetStatus(AppMessages.FileManager.FolderSomeFailedUpload(dir.Name));
                }
                catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(dir.Name, ex.Message)); }
            }
            await RefreshRemoteAsync();
            return moved;
        });
    }

    async Task<List<FileEntry>> DownloadFoldersAsync(IList<FileEntry> dirs)
    {
        var moved = new List<FileEntry>();
        if (dirs.Count == 0 || !await EnsureConnectedAsync()) return moved;
        return await RunBusyAsync(AppMessages.FileManager.Downloading(dirs.Count), async () =>
        {
            foreach (var dir in dirs)
            {
                SetStatus(AppMessages.FileManager.DownloadingFolder(dir.Name));
                try
                {
                    var dest = Path.Combine(_local.Dir, dir.Name);
                    var res  = await _ftp!.DownloadDirectory(dest, dir.FullPath, FtpFolderSyncMode.Update);
                    if (res.All(r => r.IsSuccess || r.IsSkipped)) moved.Add(dir);
                    else SetStatus(AppMessages.FileManager.FolderSomeFailedDownload(dir.Name));
                }
                catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(dir.Name, ex.Message)); }
            }
            RefreshLocal();
            return moved;
        });
    }

    // ── New Folder ────────────────────────────────────────────────────────────
    async void OnRemoteNewFolder(object s, RoutedEventArgs e)
    {
        var name = PromptInput(AppMessages.Prompts.NewFolderName, "NewFolder");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunExclusive(async () =>
        {
            if (!await EnsureConnectedAsync()) return;
            var path = $"{_remote.Dir.TrimEnd('/')}/{name}";
            if (!FtpPathSafety.FitsMaxRemotePathLength(path)) { SetStatus(FtpPathSafety.TooLongMessage(path)); return; }
            try { await _ftp!.CreateDirectory(path); await RefreshRemoteAsync(); SetStatus(AppMessages.FileManager.Created(path)); }
            catch (Exception ex) { SetStatus(AppMessages.FileManager.Failed(ex.Message)); }
        });
    }

    void OnLocalNewFolder(object s, RoutedEventArgs e)
    {
        var name = PromptInput(AppMessages.Prompts.NewFolderName, "NewFolder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(_local.Dir, name);
        try { Directory.CreateDirectory(path); RefreshLocal(); SetStatus(AppMessages.FileManager.Created(path)); }
        catch (Exception ex) { SetStatus(AppMessages.FileManager.Failed(ex.Message)); }
    }

    // ── Delete ────────────────────────────────────────────────────────────────
    async void OnRemoteDelete(object s, RoutedEventArgs e)
    {
        var items = RealItems(RemoteList.SelectedItems.Cast<FileEntry>());
        if (items.Count == 0) { SetStatus(AppMessages.FileManager.SelectItemsToDelete); return; }
        if (MessageBox.Show(AppMessages.FileManager.ConfirmDeleteRemote(items.Count), AppMessages.Titles.Delete,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunExclusive(async () =>
        {
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
                catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(item.Name, ex.Message)); }
            }
            await RefreshRemoteAsync();
            SetStatus(AppMessages.FileManager.Deleted(done, items.Count));
        });
    }

    void OnLocalDelete(object s, RoutedEventArgs e)
    {
        var items = RealItems(LocalList.SelectedItems.Cast<FileEntry>());
        if (items.Count == 0) { SetStatus(AppMessages.FileManager.SelectItemsToDelete); return; }
        if (MessageBox.Show(AppMessages.FileManager.ConfirmDeleteLocal(items.Count), AppMessages.Titles.Delete,
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
            catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(item.Name, ex.Message)); }
        }
        RefreshLocal();
        SetStatus(AppMessages.FileManager.Deleted(done, items.Count));
    }

    // ── Rename ────────────────────────────────────────────────────────────────
    async void OnRemoteRename(object s, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItems.Count != 1 || RemoteList.SelectedItem is FileEntry { Name: ParentEntryName })
            { SetStatus(AppMessages.FileManager.SelectOneToRename); return; }
        var item = (FileEntry)RemoteList.SelectedItem!;
        // Top-level entries are the Kronos's own SSD1/SSD2/SSD3/... storage volumes - see
        // FtpPathSafety's own comment for why renaming one is never allowed. Checked here,
        // ahead of even prompting for a new name, so the context menu's own disabled state
        // (BuildContextMenu) isn't the only thing standing between the user and this -
        // RenameGuardedAsync below is the last line of defense, not the first.
        if (FtpPathSafety.IsTopLevelPath(item.FullPath))
            { SetStatus(AppMessages.FileManager.CannotRenameTopLevel(item.Name)); return; }
        var newName = PromptInput(AppMessages.Prompts.NewName, item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        await RunExclusive(async () =>
        {
            if (!await EnsureConnectedAsync()) return;
            var newPath = $"{GetFtpParent(item.FullPath).TrimEnd('/')}/{newName}";
            if (!FtpPathSafety.FitsMaxRemotePathLength(newPath)) { SetStatus(FtpPathSafety.TooLongMessage(newPath)); return; }
            try { await _ftp!.RenameGuardedAsync(item.FullPath, newPath); await RefreshRemoteAsync(); SetStatus(AppMessages.FileManager.Renamed(newName)); }
            catch (Exception ex) { SetStatus(AppMessages.FileManager.RenameFailed(ex.Message)); }
        });
    }

    void OnLocalRename(object s, RoutedEventArgs e)
    {
        if (LocalList.SelectedItems.Count != 1 || LocalList.SelectedItem is FileEntry { Name: ParentEntryName })
            { SetStatus(AppMessages.FileManager.SelectOneToRename); return; }
        var item    = (FileEntry)LocalList.SelectedItem!;
        var newName = PromptInput(AppMessages.Prompts.NewName, item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        var newPath = Path.Combine(Path.GetDirectoryName(item.FullPath) ?? _local.Dir, newName);
        try
        {
            if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
            else                  File.Move(item.FullPath, newPath);
            RefreshLocal();
            SetStatus(AppMessages.FileManager.Renamed(newName));
        }
        catch (Exception ex) { SetStatus(AppMessages.FileManager.RenameFailed(ex.Message)); }
    }

    // Persists immediately (same "load fresh, mutate, save" pattern SampleEditorViewModel's own
    // recent-files methods use - this window is never handed the shared AppSettings instance),
    // so it survives even if the window is closed without any other settings-saving action.
    void SetDefaultStartFolder(string path)
    {
        var settings = Storage.LoadSettings();
        settings.FileManagerDefaultLocalFolder = path;
        Storage.SaveSettings(settings);
        SetStatus(AppMessages.FileManager.DefaultStartFolderSet(path));
    }

    // ── Drag-to-select (rubber-band) ──────────────────────────────────────────
    // The ListView's own vertical/horizontal ScrollBar lives inside its default template,
    // so a mouse-down on its thumb/track/arrows still tunnels through this Preview handler
    // first. Without this check, GetEntryAt below finds no FileEntry under the scrollbar
    // (it isn't a row) and falls into BeginRubberBand, which steals the drag - the reported
    // bug: dragging the scrollbar thumb just started a highlight-select instead of scrolling.
    static bool IsScrollBarHit(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollBar) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    void OnListPreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (IsScrollBarHit(e.OriginalSource as DependencyObject)) return; // let the ScrollBar handle its own drag

        var lv    = (ListView)s;
        var entry = GetEntryAt(lv, e.GetPosition(lv));
        _dragSource = null;

        if (entry != null)
        {
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
                _deferredSelectEntry = null; // drag wins - don't collapse selection on mouseup
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
        // No CaptureMouse - it fights with ListViewItem's press-state tracking and
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
    // these never fire in the main window and macros never fire here - naturally
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
                    _ = RunExclusive(() => DoPasteAsync(isRemote)); e.Handled = true; break;

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
                if      (remoteHas) _ = RunExclusive(() => RefreshRemoteAsync());
                else if (localHas)  RefreshLocal();
                else                { _ = RunExclusive(() => RefreshRemoteAsync()); RefreshLocal(); }
                e.Handled = true; break;

            case Key.Back when anyPane:
                if (isRemote) OnRemoteUp(null!, null!);
                else          OnLocalUp(null!, null!);
                e.Handled = true; break;

            case Key.Return when anyPane:
                if (lv!.SelectedItem is FileEntry { IsDirectory: true } dir)
                {
                    if (isRemote) _ = NavigateRemoteAsync(dir.FullPath);
                    else          { _local.Dir  = dir.FullPath; RefreshLocal(); }
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
        // Mouse left without triggering a drag or click - cancel deferred state
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
        var items = RealItems(lv.SelectedItems.Cast<FileEntry>());
        if (items.Count == 0) return; // dragging only the ".." row - nothing real to move
        var payload = new DragPayload(lv == RemoteList, items);
        var data    = new DataObject(DragDataFormat, payload);
        DragDrop.DoDragDrop(lv, data, DragDropEffects.Copy | DragDropEffects.Move);
        // Drag ended - guarantee dwell state, scroll, and button highlights are cleaned up
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

        var pos = e.GetPosition(lv);
        var sv  = lv == LocalList ? _local.ScrollViewer : _remote.ScrollViewer;
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
            var parent = GetFtpParent(_remote.Dir);
            if (parent == _remote.Dir) return; // already at root
            await RunExclusive(async () =>
            {
                _remote.Dir = parent;
                await RefreshRemoteAsync();
                var items = payload.Items.Where(f => GetFtpParent(f.FullPath) != _remote.Dir).ToList();
                if (items.Count > 0) await MoveRemoteItemsAsync(items, _remote.Dir);
            });
        }
        else if (btn == BtnLocalUp && !payload.FromRemote)
        {
            var parent = Directory.GetParent(_local.Dir)?.FullName;
            if (parent == null) return;
            _local.Dir = parent;
            RefreshLocal();
            var items = payload.Items
                .Where(f => (Path.GetDirectoryName(f.FullPath) ?? "") != _local.Dir).ToList();
            if (items.Count > 0) await MoveLocalItemsAsync(items, _local.Dir);
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
            if (lv == RemoteList) await NavigateRemoteAsync(folder.FullPath);
            else                  { _local.Dir  = folder.FullPath; RefreshLocal(); }
        }
        else if (target is Button btn)
        {
            if (btn == BtnRemoteUp)
            {
                var parent = GetFtpParent(_remote.Dir);
                if (parent != _remote.Dir) await NavigateRemoteAsync(parent);
            }
            else
            {
                var parent = Directory.GetParent(_local.Dir)?.FullName;
                if (parent != null) { _local.Dir = parent; RefreshLocal(); }
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
                ? Path.GetDirectoryName(items[0].FullPath) ?? _local.Dir
                : _local.Dir;
            if (_local.Dir != sourcePath)
                await MoveLocalItemsAsync(items, _local.Dir);
            return;
        }

        var files = items.Where(f => !f.IsDirectory).ToList();
        var dirs  = items.Where(f =>  f.IsDirectory).ToList();
        await RunExclusive(async () =>
        {
            if (files.Count > 0) await DownloadItemsAsync(files);
            if (dirs.Count  > 0) await DownloadFoldersAsync(dirs);
        });
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
            await RunExclusive(() => MoveRemoteItemsAsync(items, targetFolder.FullPath));
            return;
        }

        if (payload.FromRemote)
        {
            // Same-pane drop on empty space: move to current directory if we navigated here
            var sourcePath = items.Count > 0 ? GetFtpParent(items[0].FullPath) : _remote.Dir;
            if (_remote.Dir != sourcePath)
                await RunExclusive(() => MoveRemoteItemsAsync(items, _remote.Dir));
            return;
        }

        var files = items.Where(f => !f.IsDirectory).ToList();
        var dirs  = items.Where(f =>  f.IsDirectory).ToList();
        await RunExclusive(async () =>
        {
            if (files.Count > 0) await UploadItemsAsync(files);
            if (dirs.Count  > 0) await UploadFoldersAsync(dirs);
        });
    }

    async Task MoveLocalItemsAsync(IList<FileEntry> items, string destFolder) =>
        await RunBusyAsync(AppMessages.FileManager.MovingItems(items.Count), async () =>
    {
        int done = 0;
        var resolve = MakeConflictResolver();
        foreach (var item in items)
        {
            var fileName = item.Name;
            var dest     = Path.Combine(destFolder, fileName);

            // A no-op move onto itself would throw ("source and destination are the same").
            if (string.Equals(Path.GetFullPath(item.FullPath), Path.GetFullPath(dest),
                              StringComparison.OrdinalIgnoreCase))
                continue;

            bool exists    = item.IsDirectory ? Directory.Exists(dest) : File.Exists(dest);
            bool overwrite = false;
            if (exists)
            {
                var r = resolve(fileName);
                if (r.Action == ConflictAction.Cancel) break;
                if (r.Action == ConflictAction.Skip)   continue;
                if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = Path.Combine(destFolder, fileName); }
                else                                   overwrite = true;  // replace existing
            }
            try
            {
                // Off the UI thread - a large folder move would otherwise freeze the window.
                await Task.Run(() =>
                {
                    if (item.IsDirectory)
                    {
                        bool sameVolume = string.Equals(
                            Path.GetPathRoot(Path.GetFullPath(item.FullPath)),
                            Path.GetPathRoot(Path.GetFullPath(dest)),
                            StringComparison.OrdinalIgnoreCase);

                        if (sameVolume)
                        {
                            if (overwrite && Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                            Directory.Move(item.FullPath, dest);   // File.Move does NOT work on directories
                        }
                        else
                        {
                            // Directory.Move cannot cross volumes - it throws, and only AFTER doing
                            // the delete-then-move naively would already have destroyed dest with
                            // nothing left to replace it. Copy to a staging sibling next to dest and
                            // verify it landed BEFORE touching either the old dest or the source, so
                            // a failure at any point up to the final renames leaves both intact.
                            var staging = dest + ".stage_" + Guid.NewGuid().ToString("N")[..8];
                            try
                            {
                                CopyDirectoryRecursive(item.FullPath, staging);
                            }
                            catch
                            {
                                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
                                throw;
                            }
                            if (overwrite && Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                            Directory.Move(staging, dest);   // same-volume rename now, so this can't fail partway
                            Directory.Delete(item.FullPath, recursive: true);
                        }
                    }
                    else
                    {
                        File.Move(item.FullPath, dest, overwrite);
                    }
                });
                done++;
            }
            catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(item.Name, ex.Message)); }
        }
        RefreshLocal();
        SetStatus(AppMessages.FileManager.MovedItems(done, items.Count, destFolder));
    });

    async Task MoveRemoteItemsAsync(IList<FileEntry> items, string destFolder)
    {
        if (!await EnsureConnectedAsync()) return;
        await RunBusyAsync(AppMessages.FileManager.MovingFiles(items.Count), async () =>
        {
            int done = 0;
            foreach (var item in items)
            {
                var dest = $"{destFolder.TrimEnd('/')}/{item.Name}";
                try { await _ftp!.RenameGuardedAsync(item.FullPath, dest); done++; }
                catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(item.Name, ex.Message)); }
            }
            await RefreshRemoteAsync();
            SetStatus(AppMessages.FileManager.MovedFiles(done, items.Count, destFolder));
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    async Task<bool> EnsureConnectedAsync()
    {
        if (_ftp == null) return false;
        if (!_ftp.IsConnected)
        {
            try
            {
                SetStatus(AppMessages.FileManager.Reconnecting);
                await Task.Run(() => _ftp.Connect(CancellationToken.None));
            }
            catch (Exception ex) { SetStatus(AppMessages.FileManager.ReconnectFailed(ex.Message)); return false; }
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

    // Per-transfer conflict resolver: prompts once per name, but remembers an "apply to all"
    // choice (re-suggesting a fresh name each time for a remembered Rename).  One implementation
    // shared by every transfer loop instead of the same closure copy-pasted five times.
    Func<string, ConflictResult> MakeConflictResolver()
    {
        ConflictResult? remembered = null;
        return fn =>
        {
            if (remembered != null)
                return remembered.Action == ConflictAction.Rename
                    ? remembered with { Name = ConflictDialog.SuggestName(fn) }
                    : remembered;
            var r = AskConflict(fn);
            if (r.ApplyToAll) remembered = r;
            return r;
        };
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
        if (entry != null && !lv.SelectedItems.Contains(entry))
            lv.SelectedItem = entry;
        lv.ContextMenu = BuildContextMenu(lv, isRemote, entry);
    }

    ContextMenu BuildContextMenu(ListView lv, bool isRemote, FileEntry? entry)
    {
        bool onFolder      = entry is { IsDirectory: true };
        bool isParentEntry = entry?.Name == ParentEntryName;
        // Excludes ".." from what Cut/Copy/Rename/Delete consider "selected" - right-clicking
        // it (or Ctrl+A/rubber-band catching it alongside real items) must never offer to act
        // on it destructively; navigating INTO it via Open is unaffected (see `onFolder`, which
        // still reads the raw right-clicked entry).
        var realSelected  = RealItems(lv.SelectedItems.Cast<FileEntry>());
        bool hasSelection = realSelected.Count > 0;
        bool isSingle     = realSelected.Count == 1;
        // A remote top-level entry (SSD1/SSD2/SSD3/...) can never be cut, copied, deleted,
        // or renamed - see FtpPathSafety's own comment. Local entries have no such
        // restriction, and Open (navigating INTO the volume) is unaffected.
        bool remoteTopLevelSelected = isRemote && realSelected.Any(f => FtpPathSafety.IsTopLevelPath(f.FullPath));

        var cm = new ContextMenu();

        if (onFolder)
        {
            cm.Items.Add(MakeItem("Open", true, async (_, _) =>
            {
                if (isRemote) await NavigateRemoteAsync(entry!.FullPath);
                else          { _local.Dir  = entry!.FullPath; RefreshLocal(); }
            }));
            if (!isRemote && !isParentEntry)
                cm.Items.Add(MakeItem("Set Default Start Folder", true,
                    (_, _) => SetDefaultStartFolder(entry!.FullPath)));
        }
        else
        {
            cm.Items.Add(MakeItem(
                isRemote ? "← Send to PC" : "→ Send to Kronos",
                hasSelection,
                isRemote ? (RoutedEventHandler)OnDownload : OnUpload));
        }

        cm.Items.Add(MakeItem("Cut",   hasSelection && !remoteTopLevelSelected, (_, _) => DoCut(lv, isRemote)));
        cm.Items.Add(MakeItem("Copy",  hasSelection && !remoteTopLevelSelected, (_, _) => DoCopy(lv, isRemote)));
        cm.Items.Add(MakeItem("Paste", _clipboard != null && !_busy,
                              async (_, _) => await RunExclusive(() => DoPasteAsync(isRemote))));
        cm.Items.Add(new Separator());
        bool canRename = isSingle && entry != null && !remoteTopLevelSelected;
        cm.Items.Add(MakeItem("Rename", canRename,
                     isRemote ? (RoutedEventHandler)OnRemoteRename : OnLocalRename));
        cm.Items.Add(MakeItem("Delete", hasSelection && !remoteTopLevelSelected,
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
        var items = RealItems(lv.SelectedItems.Cast<FileEntry>());
        if (items.Count == 0) return;
        _clipboard = new ClipboardPayload(IsCut: true, FromRemote: isRemote, Items: items);
        SetStatus(AppMessages.FileManager.CutToMove(items.Count));
    }

    void DoCopy(ListView lv, bool isRemote)
    {
        var items = RealItems(lv.SelectedItems.Cast<FileEntry>());
        if (items.Count == 0) return;
        _clipboard = new ClipboardPayload(IsCut: false, FromRemote: isRemote, Items: items);
        SetStatus(AppMessages.FileManager.CopiedToCopy(items.Count));
    }

    async Task DoPasteAsync(bool toRemote)
    {
        if (_clipboard == null) return;
        var cb    = _clipboard;
        var items = cb.Items.ToList();

        if (!cb.FromRemote && !toRemote)
        {
            // Local → Local
            if (cb.IsCut) await MoveLocalItemsAsync(items, _local.Dir);
            else          await CopyLocalItemsAsync(items, _local.Dir);
            if (cb.IsCut) _clipboard = null;
        }
        else if (cb.FromRemote && toRemote)
        {
            // Remote → Remote
            if (cb.IsCut) await MoveRemoteItemsAsync(items, _remote.Dir);
            else          await CopyRemoteItemsAsync(items, _remote.Dir);
            if (cb.IsCut) _clipboard = null;
        }
        else if (!cb.FromRemote)
        {
            // Local → Remote
            var files = items.Where(f => !f.IsDirectory).ToList();
            var dirs  = items.Where(f =>  f.IsDirectory).ToList();
            var movedFiles = files.Count > 0 ? await UploadItemsAsync(files) : new List<FileEntry>();
            var movedDirs  = dirs.Count  > 0 ? await UploadFoldersAsync(dirs) : new List<FileEntry>();
            if (cb.IsCut)
            {
                // Move semantics: delete only sources whose transfer verifiably succeeded.
                foreach (var f in movedFiles) try { File.Delete(f.FullPath); }            catch (Exception ex) { AppLog.Debug($"[fm] cut cleanup {f.Name}: {ex.Message}"); }
                foreach (var d in movedDirs)  try { Directory.Delete(d.FullPath, true); } catch (Exception ex) { AppLog.Debug($"[fm] cut cleanup {d.Name}: {ex.Message}"); }
                RefreshLocal();
                RetainClipboardForUnmoved(cb, movedFiles, movedDirs);
            }
        }
        else
        {
            // Remote → Local
            var files = items.Where(f => !f.IsDirectory).ToList();
            var dirs  = items.Where(f =>  f.IsDirectory).ToList();
            var movedFiles = files.Count > 0 ? await DownloadItemsAsync(files) : new List<FileEntry>();
            var movedDirs  = dirs.Count  > 0 ? await DownloadFoldersAsync(dirs) : new List<FileEntry>();
            if (cb.IsCut)
            {
                // Move semantics: delete only remote sources whose download verifiably succeeded.
                if (await EnsureConnectedAsync())
                {
                    foreach (var f in movedFiles) try { await _ftp!.DeleteFile(f.FullPath); }      catch (Exception ex) { AppLog.Debug($"[fm] cut cleanup {f.Name}: {ex.Message}"); }
                    foreach (var d in movedDirs)  try { await _ftp!.DeleteDirectory(d.FullPath); } catch (Exception ex) { AppLog.Debug($"[fm] cut cleanup {d.Name}: {ex.Message}"); }
                    await RefreshRemoteAsync();
                }
                RetainClipboardForUnmoved(cb, movedFiles, movedDirs);
            }
        }
    }

    // After a cut/move, keep only the sources that did NOT transfer on the clipboard so the user
    // can retry them; clear it entirely once everything has moved.  `moved` items are the exact
    // FileEntry instances taken from cb.Items, so reference/value identity both match here.
    void RetainClipboardForUnmoved(ClipboardPayload cb, IEnumerable<FileEntry> movedFiles, IEnumerable<FileEntry> movedDirs)
    {
        var moved   = movedFiles.Concat(movedDirs).ToHashSet();
        var unmoved = cb.Items.Where(i => !moved.Contains(i)).ToList();
        if (unmoved.Count == 0) { _clipboard = null; return; }
        _clipboard = cb with { Items = unmoved };
        SetStatus(AppMessages.FileManager.MovedKeptOnClipboard(moved.Count, unmoved.Count));
    }

    async Task CopyLocalItemsAsync(IList<FileEntry> items, string destFolder) =>
        await RunBusyAsync(AppMessages.FileManager.CopyingItems(items.Count), async () =>
    {
        int done = 0;
        var resolve = MakeConflictResolver();
        foreach (var item in items)
        {
            var fileName = item.Name;
            var dest     = Path.Combine(destFolder, fileName);
            bool exists  = item.IsDirectory ? Directory.Exists(dest) : File.Exists(dest);
            if (exists)
            {
                var r = resolve(fileName);
                if (r.Action == ConflictAction.Cancel) break;
                if (r.Action == ConflictAction.Skip)   continue;
                if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = Path.Combine(destFolder, fileName); }
            }
            try
            {
                // Off the UI thread - a large recursive copy would otherwise freeze the window.
                await Task.Run(() =>
                {
                    if (item.IsDirectory) CopyDirectoryRecursive(item.FullPath, dest);
                    else                  File.Copy(item.FullPath, dest, overwrite: true);
                });
                done++;
            }
            catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(item.Name, ex.Message)); }
        }
        RefreshLocal();
        SetStatus(AppMessages.FileManager.Copied(done, items.Count, destFolder));
    });

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
        await RunBusyAsync(AppMessages.FileManager.CopyingItems(items.Count), async () =>
        {
            int done    = 0;
            var tempDir = Path.Combine(Path.GetTempPath(), "KronosCopy_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            var resolve = MakeConflictResolver();
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
                        var r = resolve(fileName);
                        if (r.Action == ConflictAction.Cancel) break;
                        if (r.Action == ConflictAction.Skip)   continue;
                        if (r.Action == ConflictAction.Rename) { fileName = r.Name; dest = $"{destFolder.TrimEnd('/')}/{fileName}"; }
                    }
                    try
                    {
                        if (item.IsDirectory)
                        {
                            var localDir = Path.Combine(tempDir, item.Name);
                            SetStatus(AppMessages.FileManager.CopyingFolder(item.Name));
                            await _ftp!.DownloadDirectory(localDir, item.FullPath, FtpFolderSyncMode.Update);
                            await _ftp!.UploadDirectory(localDir, dest, FtpFolderSyncMode.Update);
                        }
                        else
                        {
                            // Download side already lands in tempDir, well short of the final remote
                            // dest. Only the upload side targets it directly, so only it needs the
                            // stage-verify-promote treatment (same reasoning as UploadItemsAsync).
                            var tempPath = Path.Combine(tempDir, item.Name);
                            await _ftp!.DownloadFile(tempPath, item.FullPath, FtpLocalExists.Overwrite);
                            var part = $"{dest}.{Guid.NewGuid().ToString("N")[..8]}.part";
                            var st = await _ftp!.UploadFile(tempPath, part, FtpRemoteExists.Overwrite, createRemoteDir: true);
                            if (st != FtpStatus.Success)
                            {
                                try { await _ftp!.DeleteFile(part); } catch { }
                                throw new IOException($"upload did not complete ({st})");
                            }
                            if (await _ftp!.FileExists(dest)) await _ftp!.DeleteFile(dest);
                            await _ftp!.RenameGuardedAsync(part, dest);
                        }
                        done++;
                    }
                    catch (Exception ex) { SetStatus(AppMessages.FileManager.FailedItem(item.Name, ex.Message)); }
                }
            }
            finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
            await RefreshRemoteAsync();
            SetStatus(AppMessages.FileManager.Copied(done, items.Count, destFolder));
        });
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
        var root = Path.GetPathRoot(_local.Dir);
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
        _local.Dir = drive.RootPath;
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
        var dlg = new PromptDialog(prompt, initial).OwnedBy(this);
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ── Column sort ───────────────────────────────────────────────────────────
    void OnColumnHeaderClick(Pane pane, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h ||
            h.Column == null || h.Role != GridViewColumnHeaderRole.Normal) return;
        var col = MapColumn(pane, h.Column);
        if (col == null) return;
        if (col == pane.SortCol) pane.SortAsc = !pane.SortAsc;
        else { pane.SortCol = col.Value; pane.SortAsc = true; }
        ApplySort(pane.Items, pane.SortCol, pane.SortAsc);
        UpdateHeaders(pane);
    }

    static SortColumn? MapColumn(Pane pane, GridViewColumn col)
        => col == pane.NameCol ? SortColumn.Name
         : col == pane.SizeCol ? SortColumn.Size
         : col == pane.DateCol ? SortColumn.Modified
         : (SortColumn?)null;

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

    // ".." always floats to the top regardless of sort column/direction - filtered out before
    // sorting and re-added first, rather than made part of the sort key, so a manual column-
    // header re-sort (which calls this directly, bypassing Refresh*'s own insertion order) can't
    // scatter it into the middle of the listing.
    void ApplySort(ObservableCollection<FileEntry> items, SortColumn col, bool asc)
    {
        var parent = items.FirstOrDefault(f => f.Name == ParentEntryName);
        var sorted = SortEntries(items.Where(f => f.Name != ParentEntryName), col, asc).ToList();
        items.Clear();
        if (parent != null) items.Add(parent);
        foreach (var e in sorted) items.Add(e);
    }

    void UpdateHeaders(Pane pane)
    {
        pane.NameCol.Header = pane.NameHeader + Ind(pane.SortCol, pane.SortAsc, SortColumn.Name);
        pane.SizeCol.Header = "Size"          + Ind(pane.SortCol, pane.SortAsc, SortColumn.Size);
        pane.DateCol.Header = "Modified"      + Ind(pane.SortCol, pane.SortAsc, SortColumn.Modified);
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
