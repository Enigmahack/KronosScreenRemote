namespace KronosScreenRemote;

/// <summary>
/// The single catalog for every user-facing <b>popup, dialog, status-bar, and notification</b>
/// string in the app. Edit wording here — in one place — and it changes at every call site.
/// <para>
/// Deliberately excluded (edit those where they live): menu-item captions, tooltips / mouse-hover
/// text, XAML layout labels, window title-bar text, log-only strings (<see cref="AppLog"/>), and
/// the push pipeline's diagnostic step trace (LibrarianModel's <c>Note()</c> lines / the
/// <c>aborted</c> reply text — protocol telemetry, not polished messaging).
/// </para>
/// <para>
/// Convention: <b>fixed</b> text is a <c>const</c>; text that embeds a runtime value is a static
/// <b>method</b> that composes the final string from arguments the caller has already stringified
/// (pass <c>ex.Message</c>, a formatted count, a <c>.Label()</c> result, etc. <i>in</i>). This
/// keeps the catalog free of app/model types so anyone can edit the words without touching logic.
/// Raw exception text (bare <c>ex.Message</c>) stays at the call site; only the wrapper moves here.
/// </para>
/// </summary>
public static class AppMessages
{
    /// <summary>MessageBox titles reused by more than one call site.</summary>
    public static class Titles
    {
        public const string AuthenticationFailed = "Authentication Failed";
        public const string Delete               = "Delete";
        public const string ExportComplete       = "Export Complete";
        public const string ExportFailed         = "Export Failed";
        public const string ImportComplete       = "Import Complete";
        public const string ImportFailed         = "Import Failed";
    }

    /// <summary>Connection dialogs and the main-window status line.</summary>
    public static class Connection
    {
        // ── Connect-failure dialogs (MainWindow.Streaming.ShowConnectError) ──
        public const string TimedOutTitle = "Connection Timed Out";
        public const string FailedTitle   = "Kronos ScreenRemote";   // generic connect-fail dialog title
        public const string DaemonRejectedCredentials =
            "The Kronos daemon rejected the FTP credentials.\n\nClick Reconnect to try again.";
        public static string Failed(string detail) => $"Connection failed:\n{detail}";

        // ── No IP configured ──
        public const string NoIpTitle = "Connection";
        public const string NoIpConfigured =
            "No Kronos IP address is configured.\n\nGo to Settings and enter the Kronos IP address.";

        // ── Status line ──
        public static string Connected(string host)  => $"Connected — {host}";
        public static string Connecting(string host) => $"Connecting to {host}…";
        public const string UsbMidiScreenNotConnected = "USB MIDI — screen not connected";
        public const string NotConnected              = "Not connected";
    }

    /// <summary>FTP authentication (KronosFtpSession).</summary>
    public static class Ftp
    {
        public const string AuthFailedAfterAttempts =
            "FTP authentication failed after 3 attempts.\nTry again.";
    }

    /// <summary>Screenshot save dialog (MainWindow).</summary>
    public static class Screenshot
    {
        public const string Title = "Screenshot";
        public const string NoFrameAvailable = "No frame available — connect to Kronos first.";
        public static string SaveFailed(string detail) => $"Failed to save screenshot:\n{detail}";
    }

    /// <summary>Kronos Test Mode confirmation (MainWindow).</summary>
    public static class TestMode
    {
        public const string Title = "Kronos Test Mode";
        public const string Warning =
            "This will place you into the Kronos Test Mode. All unsaved changes will be lost, " +
            "and your Kronos will need to be restarted after complete. Also, this is potentially " +
            "a dangerous operation and should only be performed if you are aware of the risk.\n\n" +
            "Do you wish to continue?";
    }

    /// <summary>Quit / close confirmations (MainWindow.Input).</summary>
    public static class Quit
    {
        public const string Title              = "Quit";
        public const string DisconnectAndQuit  = "Disconnect from Kronos and quit?";
        public const string QuitApp            = "Quit Kronos ScreenRemote?";
    }

    /// <summary>Calibration dialogs (MainWindow.Calibration / MainWindow.Input).</summary>
    public static class Calibration
    {
        public const string GridChangeTitle = "Change Calibration Grid";
        public static string GridChangeWarning(int size) =>
            $"Changing grid size to {size}×{size} will clear existing calibration data.\nProceed?";

        public const string UnsavedTitle   = "Unsaved Calibration";
        public const string UnsavedChanges = "You have unsaved calibration changes.\nSave before exiting?";
    }

    /// <summary>Settings import/export dialog bodies shared by MainWindow and SettingsWindow.</summary>
    public static class SettingsIo
    {
        public static string Exported(string path)      => $"Settings exported to:\n{path}";
        public static string ExportFailed(string detail) => $"Export failed:\n{detail}";
        public static string ImportFailed(string detail) => $"Import failed:\n{detail}";

        // Import-complete body differs by window:
        public const string ImportedAndApplied = "Settings imported and applied.";          // MainWindow
        public const string ImportedClickOk    = "Settings imported. Click OK to apply them."; // SettingsWindow
    }

    /// <summary>Settings-reset dialogs.</summary>
    public static class SettingsReset
    {
        // Post-reset info shown by MainWindow after a reset completes.
        public const string DoneTitle = "Settings Reset";
        public const string Done =
            "All settings have been reset to defaults.\n\nCalibration data will fully take effect on the next launch.";

        // Pre-reset confirmation shown by SettingsWindow.
        public const string ConfirmTitle = "Reset All Settings";
        public const string Confirm =
            "This will permanently remove all saved settings, key mappings, calibration data, and other customizations.\n\n" +
            "The app will return to its default state. Calibration changes take full effect on next launch.\n\n" +
            "This cannot be undone. Continue?";
    }

    /// <summary>SettingsWindow keybinding / raw-key dialogs.</summary>
    public static class Keybinding
    {
        public const string ClearBindingTitle = "Clear Binding";
        public static string ClearBinding(string label) => $"Clear keybinding for '{label}'?";

        public const string NotRemappableTitle = "Not Remappable";
        public const string NotRemappable =
            "This key routes directly to a physical control-surface button and cannot be remapped.";

        public const string ModifierRequiredTitle = "Modifier Required";
        public const string ModifierRequired =
            "A macro trigger must include at least one modifier key (Ctrl, Alt, or Shift).";

        public const string HostKeyRequiredTitle = "Host Key Required";
        public const string HostKeyRequired = "Click 'Host key' and press the key you want to capture.";

        public const string InvalidCodeTitle = "Invalid Code";
        public const string InvalidRawCode   = "Raw code must be an integer from 1 to 767 (Linux keycode).";
    }

    /// <summary>File Manager — open guard, delete confirmations, and the status line.</summary>
    public static class FileManager
    {
        // ── Popups ──
        public const string OpenNotConnectedTitle = "File Manager";
        public const string OpenNotConnected =
            "Not connected to Kronos.\n\nConnect to Kronos first, then open the File Manager.";
        public static string ConfirmDeleteRemote(int count) => $"Delete {count} item(s) from Kronos?";
        public static string ConfirmDeleteLocal(int count)  => $"Delete {count} item(s)?";

        // ── Connection / listing ──
        public const string Connecting = "Connecting to Kronos FTP…";
        public const string Connected  = "Connected.";
        public static string ConnectFailed(string detail)   => $"FTP connect failed: {detail}";
        public const string Reconnecting = "Reconnecting…";
        public static string ReconnectFailed(string detail) => $"FTP reconnect failed: {detail}";
        public static string Loading(string dir)          => $"Loading {dir}…";
        public static string ItemsIn(int count, string dir) => $"{count} item(s) in {dir}";
        public static string ErrorListingRemote(string detail) => $"Error listing remote: {detail}";
        public static string ErrorListingLocal(string detail)  => $"Error listing local: {detail}";

        // ── Transfer progress (shared upload/download line) ──
        public static string ItemProgress(int index1Based, int total, string name, double percent) =>
            $"[{index1Based}/{total}] {name} — {percent:F0}%";
        public static string FailedItem(string name, string detail) => $"Failed {name}: {detail}";

        // ── Upload ──
        public const string SelectLocalFilesToUpload = "Select one or more local files to upload.";
        public static string Uploading(int count) => $"Uploading {count} file(s)…";
        public static string UploadIncomplete(string name, object status) =>
            $"Upload of {name} did not complete ({status}) — source kept";
        public static string Uploaded(int done, int total, string dir) =>
            $"Uploaded {done}/{total} file(s) → {dir}";
        public static string UploadingFolder(string name)     => $"Uploading folder {name}…";
        public static string FolderSomeFailedUpload(string name) => $"{name}: some files failed to upload — source kept";

        // ── Download ──
        public const string SelectKronosFilesToDownload = "Select one or more Kronos files to download.";
        public static string Downloading(int count) => $"Downloading {count} file(s)…";
        public static string DownloadIncomplete(string name, object status) =>
            $"Download of {name} did not complete ({status}) — source kept";
        public static string Downloaded(int done, int total, string dir) =>
            $"Downloaded {done}/{total} file(s) → {dir}";
        public static string DownloadingFolder(string name)     => $"Downloading folder {name}…";
        public static string FolderSomeFailedDownload(string name) => $"{name}: some files failed to download — source kept";

        // ── New folder / rename ──
        public static string Created(string path) => $"Created {path}";
        public static string Failed(string detail) => $"Failed: {detail}";
        public const string SelectItemsToDelete   = "Select items to delete.";
        public static string Deleted(int done, int total) => $"Deleted {done}/{total} item(s).";
        public const string SelectOneToRename = "Select exactly one item to rename.";
        public static string Renamed(string newName)       => $"Renamed → {newName}";
        public static string RenameFailed(string detail)   => $"Rename failed: {detail}";

        // ── Move / copy ──
        public static string MovingItems(int count) => $"Moving {count} item(s)…";
        public static string MovingFiles(int count) => $"Moving {count} file(s)…";
        public static string MovedItems(int done, int total, string dir) => $"Moved {done}/{total} item(s) → {dir}";
        public static string MovedFiles(int done, int total, string dir) => $"Moved {done}/{total} file(s) → {dir}";
        public static string MovedKeptOnClipboard(int moved, int unmoved) =>
            $"Moved {moved} item(s); kept {unmoved} on clipboard (transfer skipped or failed).";
        public static string CutToMove(int count)    => $"Cut {count} item(s) — paste to move.";
        public static string CopiedToCopy(int count) => $"Copied {count} item(s) — paste to copy.";
        public static string CopyingItems(int count) => $"Copying {count} item(s)…";
        public static string CopyingFolder(string name) => $"Copying folder {name}…";
        public static string Copied(int done, int total, string dir) => $"Copied {done}/{total} item(s) → {dir}";
    }

    /// <summary>The MainWindow notification bubble (transient status, click opens the log).</summary>
    public static class Notify
    {
        public const string NoFrameToSave = "No frame to save — connect first";
        public const string NoFrameToCopy = "No frame to copy — connect first";
        public const string FrameCopied   = "Frame copied to clipboard";
        public static string Saved(string fileName)            => $"Saved {fileName}";
        public static string ScreenshotFailed(string detail)   => $"Screenshot failed: {detail}";
        public static string CopyFailed(string detail)         => $"Copy failed: {detail}";
        public static string CouldNotOpenFolder(string detail) => $"Could not open folder: {detail}";

        // Bubble tooltip chrome shown around every notification / when idle.
        public const string LogHintSuffix   = "\n— click to open log";
        public const string ClickToOpenLog  = "Click to open log";
    }

    /// <summary>Text-input prompt dialogs (PromptDialog).</summary>
    public static class Prompts
    {
        public const string NewFolderName = "New folder name:";   // FileManagerWindow
        public const string NewName       = "New name:";          // FileManagerWindow
        public static string Rename(string label) => $"Rename {label}:";   // LibrarianShellWindow
    }

    /// <summary>FTP login dialog (LoginDialog).</summary>
    public static class Login
    {
        public static string Subtitle(string host, int port) => $"FTP credentials for {host}:{port}";
        public const string UsernameRequired = "Username is required.";
        public static string Failed(string detail) => $"Login failed: {detail}";
        public static string AttemptsRemaining(int remaining) =>
            $" ({remaining} attempt{(remaining == 1 ? "" : "s")} remaining)";
    }

    /// <summary>The Librarian window — panes, drop targets, sync/commit, and pipeline progress.</summary>
    public static class Librarian
    {
        // Fallback outcome tokens used when an operation returns no more-specific message.
        public const string Pasted      = "Pasted.";
        public const string PasteFailed = "Paste failed.";
        public const string Placed      = "Placed.";
        public const string PlaceFailed = "Place failed.";

        /// <summary>Local Library pane — clipboard, paste/move results, per-item edit status.</summary>
        public static class Local
        {
            // Shown in place of the tree while the referrer catalog is (re)indexing — editing is
            // blocked until it finishes, so the tree isn't shown against a half-built index.
            public const string IndexingPlaceholder =
                "Indexing local library…\nThe library will appear here once indexing is complete.";

            public const string NothingToCut = "Nothing selected to cut.";
            public const string CutOneAtATime =
                "Cut works on one item at a time — select a single item, or use Copy for multiple.";
            public static string Cut(string label) => $"Cut {label} — select an occupied slot and Paste to swap.";
            public static string CopiedOne(string label) => $"Copied {label} — select a destination and Paste.";
            public static string CopiedMany(int count)   => $"Copied {count} item(s) — select a destination and Paste.";

            // Paste/move result reasons (returned to the drop handler, shown as pane status).
            public const string NothingCutOrCopied = "nothing cut or copied";
            public const string TypeMismatch       = "can't paste here — object type doesn't match";
            public const string CutNeedsOccupiedSlot =
                "Cut needs a specific occupied slot to swap into — drop directly onto one, or use Copy to fill empty slots.";
            public const string SameLocation = "source and destination are the same location";
            public static string NotFoundLocally(string label) => $"{label} not found locally";
            public static string CopiedTo(string src, string dest) => $"Copied {src} to {dest}";
            public static string CopyFailed(string? error)         => $"Copy failed: {error}";
            public static string Swapped(string src, string dest)  => $"Moved {src} ↔ {dest}";
            public static string MoveFailed(string? error)         => $"Move failed: {error}";
            public static string EmptySlotCut(string dest) =>
                $"{dest} is empty — Cut can only be pasted onto an occupied slot (to swap). Use Copy to place a copy there instead.";
            public const string NothingToPaste       = "nothing to paste";
            public const string NothingCouldBePlaced = "nothing could be placed (bank full or type mismatch)";
            public static string PlacedCount(int placed, int stillPending) =>
                $"Placed {placed}" + (stillPending > 0 ? $"; {stillPending} didn't fit (bank full or type mismatch)" : "");

            public static string Renamed(string label, string newName) => $"Renamed {label} to \"{newName}\"";
            public static string RenameFailed(string? error) => $"Rename failed: {error}";
            public static string Discarded(string label)    => $"Discarded {label}";
            public static string DiscardFailed(string? error) => $"Discard failed: {error}";
            public static string DiscardedCount(int ok, int total) =>
                ok == total ? $"Discarded {ok} item(s)" : $"Discarded {ok}/{total} item(s)";
            public static string MarkedForDeletion(string label) => $"Marked {label} for deletion";
            public static string Restored(string label)          => $"Restored {label}";
            public static string DeleteRestoreFailed(bool markForDeletion, string? error) =>
                $"{(markForDeletion ? "Delete" : "Restore")} failed: {error}";
            public static string MarkedRestoredCount(bool markForDeletion, int ok, int total)
            {
                string verb = markForDeletion ? "Marked" : "Restored";
                return ok == total ? $"{verb} {ok} item(s)" : $"{verb} {ok}/{total} item(s)";
            }
            public const string NothingToClear = "Nothing to clear.";
            public static string ClearedChanges(int ok) => $"Cleared {ok} pending change(s).";
            public static string Edited(string label)   => $"Edited {label}";
            public static string EditFailed(string? error) => $"Edit failed: {error}";
            public static string EditedSlot(string label, int slot) => $"Edited {label} slot {slot}";
        }

        /// <summary>Merge Window pane.</summary>
        public static class Merge
        {
            public static string PulledIntoMerge(int count) => $"Pulled {count} object(s) into the Merge Window.";
            public static string PulledWithGapsInPcg(int added, int gaps) =>
                $"Pulled {added} object(s); {gaps} dependency reference(s) not found in this PCG — load another PCG that has them and pull it in.";
            public static string PulledWithGapsLocally(int added, int gaps) =>
                $"Pulled {added} object(s); {gaps} dependency reference(s) not found locally — pull them in too, or place from a PCG.";
            public const string Cleared    = "Merge Window cleared.";
            public const string RemovedOne = "Removed 1 item from the Merge Window.";
            public static string RemovedMany(int removed) => $"Removed {removed} item(s) from the Merge Window.";
        }

        /// <summary>PCG pane / remote-PCG picker load results.</summary>
        public static class Pcg
        {
            public static string LoadFailed(string detail)   => $"Load failed: {detail}";
            public static string NotRecognizedPcg(string fileName) => $"{fileName} is not a recognizable Kronos .pcg file.";
            public static string Loaded(string fileName, int count) => $"Loaded {fileName} — {count} object(s).";
            public static string RejectedBanksSuffix(int count) => $" ({count} bank chunk(s) couldn't be read — see log)";
            public const string FtpLoginFailedOrCancelled = "FTP login failed or was cancelled.";
            public const string LoadFromKronosCancelled =
                "Load from Kronos cancelled — the previously loaded file (if any) is unchanged.";
        }

        /// <summary>Shell — destructive confirmations, drop-target hints, sync/commit status.</summary>
        public static class Shell
        {
            // ── Sync-row status (LibrarianShellViewModel) ──
            public const string Indexing        = "Indexing local library…";
            public const string IndexingFailed  = "Local library indexing failed — see log";
            public static string SyncResult(int fetched, int conflicts, int written, int deleted) =>
                $"Pulled {fetched} object(s) ({conflicts} conflict(s)). Pushed {written} object(s)."
                + (deleted > 0 ? $" Deleted {deleted}." : "");
            public static string CommitResult(int written, int deleted) =>
                $"Pushed {written} object(s)." + (deleted > 0 ? $" Deleted {deleted}." : "");
            public const string CommitFailed         = "Commit failed — see warning.";
            public const string CancelledPendingDeps = "Cancelled — unresolved dependencies still pending.";

            // ── Destructive confirmations (code-behind) ──
            public const string ClearHistoryTitle = "Clear History";
            public const string ClearHistory =
                "Permanently delete the local edit history log?\n\nThis does not affect your current local library, pending edits, or hardware — only the History panel's audit trail, which can't be recovered afterward.";

            public const string ClearChangesTitle = "Clear Changes";
            public const string ClearChanges =
                "Revert every pending local edit back to baseline and un-mark every pending deletion?\n\nHardware is unaffected either way — this only reverts what hasn't been pushed yet. A fresh Pull would leave the library looking the same as this does.";

            public const string ClearMergeTitle = "Clear Merge";
            public const string ClearMerge =
                "Abandon everything currently staged in the Merge Window?\n\nAnything already placed into Local Library is unaffected — this only clears what's still pending.";

            public const string DeleteDependencyTitle = "Delete a dependency?";
            public static string DeleteDependencyLine(string loc, string reference) => $"  • {loc} — used by {reference}";
            public static string DeleteDependencyMore(int more) => $"\n  … and {more} more";
            public static string DeleteDependency(string list) =>
                $"You're about to delete object(s) that other Combis/Set Lists (or Songs) depend on:\n\n{list}\n\n" +
                "Deleting them may leave those Combis/Set Lists/Songs not playing correctly. Consider backing them up " +
                "elsewhere first (e.g. copy them into the Merge Window or another bank).\n\nDelete anyway?";

            public const string ChangeBankTypeTitle = "Change bank type";
            public static string ChangeBankType(string bankLabel, string curType, string newType) =>
                $"{bankLabel} is currently {curType}, but you're copying a {newType} bank into it.\n\n" +
                $"Changing the bank type ERASES everything currently in {bankLabel} on the Kronos and replaces it with this whole bank. This takes effect on Commit.\n\nProceed?";

            // ── Drop-target status hints ──
            public const string DropNotRecognizedLibraryObject = "Drop didn't carry a recognized library object.";
            public const string DropNotRecognizedMergeObject   = "Drop didn't carry a recognized Merge Window object.";
            public const string DropOutsideRow =
                "Drop landed outside any bank/slot row — try dropping directly on one.";
            public const string DropOntoBankOrSlot = "Drop onto a specific bank or slot.";
            public const string DropOntoSpecificSlot = "Drop directly onto a specific slot — pick exactly where this lands.";
            public const string DropOntoSlotOrBankForGroup =
                "Drop onto a specific slot or bank so the group has somewhere to land.";
            public const string DragMoveOneAtATime =
                "Drag-move works one item at a time — select a single item, or hold Ctrl to copy.";
            public const string SelectSlotOrBankToPasteInto = "Select a slot or bank to paste into.";
            public const string BankTypeChangeCancelled = "Bank type change cancelled.";
            public static string PlacedAt(string what, string where) => $"Placed {what} at {where}";
            public static string PlacedAtWhere(string where)         => $"Placed at {where}";
            public static string PlaceFailedDetail(string? error)    => $"Place failed: {error}";
        }

        /// <summary>Pull/push pipeline (Core) — progress and referential REFUSE warnings.
        /// The <c>"REFUSE:"</c> prefix is load-bearing (ChangesetModel.IsRefusable keys off it) — keep it.</summary>
        public static class Sync
        {
            public static string BulkDumping(string display, string bankLabel) =>
                $"Bulk-dumping {display} {bankLabel}…";
            public static string Pulling(int done, int total, string display, string bankLabel, int number) =>
                $"Pulling {done}/{total} — {display} {bankLabel}:{number:D3}";

            public static string RefusePendingDependencies(int count) =>
                $"REFUSE: {count} dependency(ies) still pending in the session clipboard — place them before pushing";
            public static string RefuseMissingReference(string loc, string missingRef, object kind) =>
                $"REFUSE: {loc} references {missingRef} ({kind}), which does not exist locally";
            public static string RefuseBankTypeMismatch(string bankLabel, string bankType) =>
                $"REFUSE: {bankLabel} is an {bankType} bank, " +
                $"but the pending Program(s) are the wrong format for it. Copy the whole bank with a type change " +
                $"(drag the bank onto it), or place them in a matching bank.";

            public const string CheckNothingToPush = "CHECK: nothing to push — no local changes are pending";
            public const string CheckEveryChangeConflicted =
                "CHECK: every pending change conflicted or was rejected — nothing left to push";
        }

        /// <summary>Move/placement planner gate reasons (BatchMoveModel / LibrarianModel PlanMove).
        /// Surfaced as pane status when a drag/paste is refused. The <c>"REFUSE:"</c> prefix is
        /// load-bearing (ChangesetModel.IsRefusable / BatchMovePlan gating keys off it) — keep it.
        /// These are deliberately technical (they cite HD-1/EXi bank types, wire byte sizes, and
        /// func reply codes) because that detail is what tells the user how to fix the refusal.</summary>
        public static class Move
        {
            public const string NoPlacements    = "REFUSE: no placements to perform";
            public static string DuplicateDestination(string label, int count) =>
                $"REFUSE: duplicate destination {label} targeted by {count} placement(s)";
            public const string BatchTypeMismatch =
                "REFUSE: batch contains an object of a different type than the batch's object type";
            public const string DestinationReadOnly = "REFUSE: a destination bank is read-only (GM/g)";
            public static string BankTypesDiffer(string fromLabel, string fromType, string toLabel, string toType) =>
                $"REFUSE: {fromLabel} ({fromType}) cannot move to {toLabel} ({toType}) — bank types differ";
            public static string CheckCrossBankUnverified(string fromLabel, string toLabel) =>
                $"CHECK: {fromLabel} -> {toLabel} crosses banks whose HD-1/EXi type couldn't be fully verified — the write may be rejected (Reply 64).";
            public static string WrongFormatForBank(string toLabel, string bankType, int expectedLen, string sourceLabel, int actualLen) =>
                $"REFUSE: {toLabel} is a {bankType} bank ({expectedLen}-byte Programs), but {sourceLabel} is {actualLen} bytes — wrong format for this bank.";
            public static string CheckDestTypeUnverified(string toLabel) =>
                $"CHECK: {toLabel}'s HD-1/EXi type couldn't be fully verified — the write may be rejected (Reply 64).";
            public static string AlreadyContainsExact(string toLabel) =>
                $"REFUSE: {toLabel} already contains this exact object — nothing to place.";
            public static string ReferencedWouldBeOverwritten(string toLabel, int refCount) =>
                $"REFUSE: {toLabel} is referenced by {refCount} object(s) and would be overwritten without being relocated itself — add it to this batch as a source, or choose a different destination.";
            public static string CheckOverwrittenNotDiverted(string toLabel) =>
                $"CHECK: {toLabel} is overwritten and not diverted — its prior contents are only recoverable from the automatic backup.";
            public static string ReferringObjectMissing(int refObj, int refBank, int refIndex) =>
                $"REFUSE: referring object missing from catalog (obj {refObj:X2} bank {refBank:X2} idx {refIndex}) — re-scan before moving";

            public const string CannotMoveBetweenTypes =
                "REFUSE: cannot move between different object types (program vs combi)";
            public static string DestinationReadOnlyBank(string dstLabel) =>
                $"REFUSE: destination {dstLabel} is a read-only (GM/g) program bank";
            public const string SameLocation = "REFUSE: source and destination are the same location";
            public const string CheckProgramMoveAcrossBanks =
                "CHECK: program move across banks — destination bank must be the same type (HD-1/EXi) or the write is rejected (Reply 64).";
        }
    }

    /// <summary>Unresolved-dependencies gate dialog (UnresolvedDependenciesDialog).</summary>
    public static class UnresolvedDependencies
    {
        public static string Heading(int count) =>
            $"{count} dependency reference{(count == 1 ? "" : "s")} still unresolved and will sound wrong until placed:\n\n" +
            "Continue anyway, or cancel to fix them first (e.g. place the staged dependency in the Merge Window)?";
        public static string Row(string label, int count) =>
            $"{label} — needed by {count} object{(count == 1 ? "" : "s")}";
    }

    /// <summary>Remote file picker (RemoteFilePickerDialog) status line.</summary>
    public static class RemoteFilePicker
    {
        public const string Connecting = "Connecting…";
        public static string ConnectFailed(string detail) => $"Connect failed: {detail}";
        public const string Loading = "Loading…";
        public static string ItemCount(int count) => $"{count} item(s)";
        public static string Error(string detail) => $"Error: {detail}";
        public static string Downloading(string name) => $"Downloading {name}…";
        public static string DownloadFailed(string detail) => $"Download failed: {detail}";
    }

    /// <summary>Input tester tool (InputTesterWindow) — field prompts and status feedback.</summary>
    public static class InputTester
    {
        public const string HostKeyPlaceholder = "[click, then press key]";
        public const string PressAKey          = "[press a key]";
        public const string ClickHostKeyField  = "Click the host-key field and press the key you want to assign.";
        public const string EnterValidRawCode  = "Enter a valid raw keycode (1–767) first.";
        public static string Mapped(string keyStr, int code) => $"Mapped: {keyStr} → KEY {code}  (active immediately)";
        public static string Saved(string path)     => $"Saved → {path}";
        public static string SaveFailed(string detail) => $"Save failed: {detail}";
        public const string NoResultsFile = "No results file found.";
        public const string ParseError    = "Parse error.";
        public static string Loaded(int count, string path) => $"Loaded {count} entries — {path}";
        public static string TestedProgress(int tested, int total) => $"{tested} / {total} tested";
    }

    /// <summary>Macro editor step hints (SettingsWindow).</summary>
    public static class Macro
    {
        public const string Recording       = "(recording — press keys…)";
        public const string NoStepsRecorded = "(no steps recorded)";
    }
}
