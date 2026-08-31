namespace KronosScreenRemote;

/// <summary>
/// The single catalog for every user-facing <b>popup, dialog, status-bar, and notification</b>
/// string in the app. Edit wording here - in one place - and it changes at every call site.
/// <para>
/// Deliberately excluded (edit those where they live): menu-item captions, tooltips / mouse-hover
/// text, XAML layout labels, window title-bar text, log-only strings (<see cref="AppLog"/>), and
/// the push pipeline's diagnostic step trace (LibrarianModel's <c>Note()</c> lines / the
/// <c>aborted</c> reply text - protocol telemetry, not polished messaging).
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
        public static string Connected(string host)  => $"Connected - {host}";
        public static string Connecting(string host) => $"Connecting to {host}...";
        public const string UsbMidiScreenNotConnected = "USB MIDI - screen not connected";
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
        public const string NoFrameAvailable = "No frame available - connect to Kronos first.";
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

    /// <summary>File Manager - open guard, delete confirmations, and the status line.</summary>
    public static class FileManager
    {
        // ── Popups ──
        public const string OpenNotConnectedTitle = "File Manager";
        public const string OpenNotConnected =
            "Not connected to Kronos.\n\nConnect to Kronos first, then open the File Manager.";
        public static string ConfirmDeleteRemote(int count) => $"Delete {count} item(s) from Kronos?";
        public static string ConfirmDeleteLocal(int count)  => $"Delete {count} item(s)?";

        // ── Connection / listing ──
        public const string Connecting = "Connecting to Kronos FTP...";
        public const string Connected  = "Connected.";
        public static string ConnectFailed(string detail)   => $"FTP connect failed: {detail}";
        public const string Reconnecting = "Reconnecting...";
        public static string ReconnectFailed(string detail) => $"FTP reconnect failed: {detail}";
        public static string Loading(string dir)          => $"Loading {dir}...";
        public static string ItemsIn(int count, string dir) => $"{count} item(s) in {dir}";
        public static string ErrorListingRemote(string detail) => $"Error listing remote: {detail}";
        public static string ErrorListingLocal(string detail)  => $"Error listing local: {detail}";

        // ── Transfer progress (shared upload/download line) ──
        public static string ItemProgress(int index1Based, int total, string name, double percent) =>
            $"[{index1Based}/{total}] {name} - {percent:F0}%";
        public static string FailedItem(string name, string detail) => $"Failed {name}: {detail}";

        // ── Upload ──
        public const string SelectLocalFilesToUpload = "Select one or more local files or folders to upload.";
        public static string Uploading(int count) => $"Uploading {count} file(s)...";
        public static string UploadIncomplete(string name, object status) =>
            $"Upload of {name} did not complete ({status}) - source kept";
        public static string Uploaded(int done, int total, string dir) =>
            $"Uploaded {done}/{total} file(s) → {dir}";
        public static string UploadingFolder(string name)     => $"Uploading folder {name}...";
        public static string FolderSomeFailedUpload(string name) => $"{name}: some files failed to upload - source kept";

        // ── Download ──
        public const string SelectKronosFilesToDownload = "Select one or more Kronos files or folders to download.";
        public static string Downloading(int count) => $"Downloading {count} file(s)...";
        public static string DownloadIncomplete(string name, object status) =>
            $"Download of {name} did not complete ({status}) - source kept";
        public static string Downloaded(int done, int total, string dir) =>
            $"Downloaded {done}/{total} file(s) → {dir}";
        public static string DownloadingFolder(string name)     => $"Downloading folder {name}...";
        public static string FolderSomeFailedDownload(string name) => $"{name}: some files failed to download - source kept";

        // ── New folder / rename ──
        public static string Created(string path) => $"Created {path}";
        public static string Failed(string detail) => $"Failed: {detail}";
        public const string SelectItemsToDelete   = "Select items to delete.";
        public static string Deleted(int done, int total) => $"Deleted {done}/{total} item(s).";
        public const string SelectOneToRename = "Select exactly one item to rename.";
        public static string Renamed(string newName)       => $"Renamed → {newName}";
        public static string RenameFailed(string detail)   => $"Rename failed: {detail}";

        // ── Move / copy ──
        public static string MovingItems(int count) => $"Moving {count} item(s)...";
        public static string MovingFiles(int count) => $"Moving {count} file(s)...";
        public static string MovedItems(int done, int total, string dir) => $"Moved {done}/{total} item(s) → {dir}";
        public static string MovedFiles(int done, int total, string dir) => $"Moved {done}/{total} file(s) → {dir}";
        public static string MovedKeptOnClipboard(int moved, int unmoved) =>
            $"Moved {moved} item(s); kept {unmoved} on clipboard (transfer skipped or failed).";
        public static string CutToMove(int count)    => $"Cut {count} item(s) - paste to move.";
        public static string CopiedToCopy(int count) => $"Copied {count} item(s) - paste to copy.";
        public static string CopyingItems(int count) => $"Copying {count} item(s)...";
        public static string CopyingFolder(string name) => $"Copying folder {name}...";
        public static string Copied(int done, int total, string dir) => $"Copied {done}/{total} item(s) → {dir}";
    }

    /// <summary>The MainWindow notification bubble (transient status, click opens the log).</summary>
    public static class Notify
    {
        public const string NoFrameToSave = "No frame to save - connect first";
        public const string NoFrameToCopy = "No frame to copy - connect first";
        public const string FrameCopied   = "Frame copied to clipboard";
        public static string Saved(string fileName)            => $"Saved {fileName}";
        public static string ScreenshotFailed(string detail)   => $"Screenshot failed: {detail}";
        public static string CopyFailed(string detail)         => $"Copy failed: {detail}";
        public static string CouldNotOpenFolder(string detail) => $"Could not open folder: {detail}";

        // Bubble tooltip chrome shown around every notification / when idle.
        public const string LogHintSuffix   = "\n- click to open log";
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

    /// <summary>The Librarian window - panes, drop targets, sync/commit, and pipeline progress.</summary>
    public static class Librarian
    {
        // Fallback outcome tokens used when an operation returns no more-specific message.
        public const string Pasted      = "Pasted.";
        public const string PasteFailed = "Paste failed.";
        public const string Placed      = "Placed.";
        public const string PlaceFailed = "Place failed.";

        /// <summary>Local Library pane - clipboard, paste/move results, per-item edit status.</summary>
        public static class Local
        {
            // Shown in place of the tree while the referrer catalog is (re)indexing - editing is
            // blocked until it finishes, so the tree isn't shown against a half-built index.
            public const string IndexingPlaceholder =
                "Indexing local library...\nThe library will appear here once indexing is complete.";

            // Shown in place of the tree when the local library holds no objects at all - a
            // fresh install, or the exe run from a folder that has no library beside it (DataDir
            // is the exe's own directory). The tree (even its bare type-root headers) stays
            // hidden until the first Sync populates it, so an empty library reads as "nothing
            // synced yet" rather than a broken, un-expandable tree.
            public const string EmptyLibraryHint =
                "Your local library is empty.\nClick “Sync Library” to pull it from your Kronos.";

            public const string NothingToCut = "Nothing selected to cut.";
            public const string CutOneAtATime =
                "Cut works on one item at a time - select a single item, or use Copy for multiple.";
            public static string Cut(string label) => $"Cut {label} - select an occupied slot and Paste to swap.";
            public static string CopiedOne(string label) => $"Copied {label} - select a destination and Paste.";
            public static string CopiedMany(int count)   => $"Copied {count} item(s) - select a destination and Paste.";

            // Paste/move result reasons (returned to the drop handler, shown as pane status).
            public const string NothingCutOrCopied = "nothing cut or copied";
            public const string TypeMismatch       = "can't paste here - object type doesn't match";
            public const string CutNeedsOccupiedSlot =
                "Cut can only swap onto an occupied slot - use Copy for empty ones.";
            public const string SameLocation = "source and destination are the same location";
            public static string NotFoundLocally(string label) => $"{label} not found locally";
            public static string CopiedTo(string src, string dest) => $"Copied {src} to {dest}";
            public static string CopyFailed(string? error)         => $"Copy failed: {error}";
            public static string Swapped(string src, string dest)  => $"Moved {src} ↔ {dest}";
            public static string MoveFailed(string? error)         => $"Move failed: {error}";
            public static string EmptySlotCut(string dest) =>
                $"{dest} is empty - Cut swaps onto occupied slots only. Use Copy instead.";
            // A drop/paste aimed at a read-only factory bank (GM, g(1)-g(9), g(d)). Those banks are
            // shown so their content can be browsed, but the instrument has no way to write to them.
            public static string ReadOnlyBank(string bankLabel) =>
                $"{bankLabel} is a read-only factory bank - choose a user or internal bank.";
            // A drop/paste onto a type-root header ("Programs"/"Combis"/"Set Lists") found no bank
            // with a free slot at all - requirement 6's one genuine failure case.
            //
            // incomingIsExi (Programs only) names the HD-1/EXi format the search was confined to
            // (LocalEditOps.FindBankWithFreeSlot). Without it the message reads as a flat lie in
            // exactly the case it fires: the user can see empty slots in banks of the OTHER format,
            // so "every Program bank is full" looks wrong and the real reason stays invisible.
            public static string NoRoomInAnyBank(string typeName, bool? incomingIsExi = null) =>
                incomingIsExi is bool exi
                    ? $"every {(exi ? "EXi" : "HD-1")} {typeName} bank is full - free a slot or pick a bank"
                    : $"every {typeName} bank is full - free a slot or pick a bank";
            // One specific bank was targeted and has no free slot - distinct from NoRoomInAnyBank,
            // which is about the header drop's library-wide search.
            public static string BankIsFull(string bankLabel) =>
                $"{bankLabel} is full - no free slots left; drop onto another bank instead";
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
            // Deliberately says nothing about WHAT wasn't found: a gap is just as often a top-level
            // pull whose slot the source doesn't have (MergeCache's Gaps carries Reason "pull" for
            // those) as it is an unresolved dependency of something that did come in.
            public static string PulledWithGapsInPcg(int staged, int gaps) =>
                $"Pulled {staged} object(s); {gaps} not found in this PCG.";
            // Same report, split by whether the user can still satisfy the miss without hunting
            // down another PCG - a gap whose address Local Library already fills needs no file at
            // all, so lumping it in with the genuinely missing ones sends the user looking for
            // objects they already have.
            public static string PulledWithGapsPartlyLocal(int staged, int gaps, int inLibrary, int stillMissing) =>
                $"Pulled {staged} object(s); {gaps} not found in this PCG. {inLibrary} found in Local Library, {stillMissing} still missing.";
            public static string PulledWithGapsLocally(int staged, int gaps) =>
                $"Pulled {staged} object(s); {gaps} not found locally.";
            public const string Cleared    = "Merge Window cleared.";
            public const string RemovedOne = "Removed 1 item from the Merge Window.";
            public static string RemovedMany(int removed) => $"Removed {removed} item(s) from the Merge Window.";

            // ── Auto-Fill (LibrarianShellViewModel.AutoFillFromMerge) ──
            // Staging only: everything it reports has landed in Local Library, not on the
            // instrument, so the wording must never read as "pushed".
            public const string AutoFillNothingStaged = "Nothing staged in the Merge Window to auto-fill.";
            // Shown per bank as the sweep runs (LibrarianShellViewModel.AutoFillToLibraryAsync's
            // pump), so a long fill reads as progress rather than as a hang.
            public static string AutoFillProgress(string what, string bankLabel, int remaining) =>
                $"Auto-Fill: placing {what}(s) into {bankLabel} - {remaining} to go...";
            // `resolved` lumps together items written into a free slot and items whose content
            // already existed elsewhere locally (reused rather than copied a second time) - the
            // per-item split isn't tracked, and from the user's side both mean the same thing:
            // that item no longer needs a slot.
            public static string AutoFillResult(int resolved, int stillStaged)
            {
                string msg = $"Auto-Fill placed {resolved} item(s) - review, then Commit Changes.";
                if (stillStaged > 0) msg += $" {stillStaged} didn't fit and stay staged.";
                return msg;
            }
            // Auto-Fill ran out of destination slots. Names the KIND that has nowhere to go (EXi
            // and HD-1 Programs are counted separately - they can't share a bank), because "the
            // destination is full" without saying full OF WHAT leaves the user hunting through
            // every bank of every type for the free space that isn't there.
            public static string AutoFillNoRoom(IReadOnlyList<(string What, int Count)> noRoom) =>
                "Local Library has no free slots left for: " +
                string.Join(", ", noRoom.Select(n => $"{n.Count} {n.What}(s)")) +
                ". These stay staged - free slots in a bank of the matching type, then run Auto-Fill again.";

            // A refusal partway through still leaves everything placed BEFORE it sitting in Local
            // Library as pending edits. Leading with that count is what tells the user whether to
            // keep the partial result or Ctrl+Z the whole sweep - a bare "stopped on X" reads as
            // "nothing happened", which is the one thing it never means.
            public static string AutoFillRefused(int resolved, string what, string? error) =>
                (resolved > 0
                    ? $"Auto-Fill placed {resolved} item(s), then stopped on {what}: "
                    : $"Auto-Fill stopped on {what}: ") + error;
        }

        /// <summary>PCG pane / remote-PCG picker load results.</summary>
        public static class Pcg
        {
            public static string LoadFailed(string detail)   => $"Load failed: {detail}";
            public static string NotRecognizedPcg(string fileName) => $"{fileName} is not a recognizable Kronos .pcg file.";
            public static string Loaded(string fileName, int count) => $"Loaded {fileName} - {count} object(s).";
            public static string RejectedBanksSuffix(int count) => $" ({count} bank chunk(s) couldn't be read - see log)";
            public static string ChecksumWarningsSuffix(int count) => $" ({count} bank chunk(s) failed their checksum - see log)";
            public const string FtpLoginFailedOrCancelled = "FTP login failed or was cancelled.";
            public const string LoadFromKronosCancelled =
                "Load from Kronos cancelled - the previously loaded file (if any) is unchanged.";
        }

        /// <summary>Shell - destructive confirmations, drop-target hints, sync/commit status.</summary>
        public static class Shell
        {
            // ── Sync-row status (LibrarianShellViewModel) ──
            public const string Indexing        = "Indexing local library...";
            public const string IndexingFailed  = "Local library indexing failed - see log";
            public static string SyncResult(int fetched, int conflicts, int written, int deleted) =>
                $"Pulled {fetched} object(s) ({conflicts} conflict(s)). Pushed {written} object(s)."
                + (deleted > 0 ? $" Deleted {deleted}." : "");
            // Pull succeeded and nothing was locally dirty to push back - a normal, successful
            // outcome, not the CHECK/warning ChangesetBuilder's early-return produces for the same
            // state (that warning is meant for Commit Changes, where "nothing to push" with no
            // preceding pull is more likely a mistaken click).
            public static string SyncComplete(bool full, int fetched, int conflicts) =>
                $"{(full ? "Full Sync" : "Sync")} Complete - pulled {fetched} object(s)"
                + (conflicts > 0 ? $" ({conflicts} conflict(s))" : "") + ", nothing to push.";
            public static string CommitResult(int written, int deleted) =>
                $"Pushed {written} object(s)." + (deleted > 0 ? $" Deleted {deleted}." : "");
            public const string CommitFailed         = "Commit failed - see warning.";
            public const string CancelledPendingDeps = "Cancelled - unresolved dependencies still pending.";
            // A Sync/Commit that threw partway (as opposed to a clean push failure returning an
            // error) - surfaced so the operation doesn't look like it silently did nothing.
            public static string OperationFailed(string detail) => $"Operation failed - {detail}";

            // ── Destructive confirmations (code-behind) ──
            public const string ClearHistoryTitle = "Clear History";
            public const string ClearHistory =
                "Permanently delete the local edit history log?\n\n" +
                "This action cannot be undone.\n\n" +
                "Continue?";

            public const string ClearChangesTitle = "Clear Changes";
            public const string ClearChanges =
                "Revert every pending local edit back to baseline and un-mark every pending deletion?";

            public const string ClearMergeTitle = "Clear Merge";
            public const string ClearMerge =
                "Clear pending changes staged in the Merge Window?";

            public const string DeleteDependencyTitle = "Delete a dependency?";
            public static string DeleteDependencyLine(string loc, string reference) => $"  • {loc} - used by {reference}";
            public static string DeleteDependencyMore(int more) => $"\n  ... and {more} more";
            public static string DeleteDependency(string list) =>
                $"You're about to delete object(s) that have parental dependcies:\n\n{list}\n\n" +
                "Deleting may leave dependents in an unfinished/unplayable state. \n\n" +
                "Delete anyway?";

            public const string ChangeBankTypeTitle = "Change bank type";
            public static string ChangeBankType(string bankLabel, string curType, string newType) =>
                $"{bankLabel} is currently {curType}, but you're copying a {newType} bank into it.\n\n" +
                $"Changing the bank type ERASES everything currently in {bankLabel} on the Kronos and replaces it with this whole bank. This takes effect on Commit.\n\n" +
                $"Proceed?";

            // Cross-pane placement gate (Merge Window / Loaded PCG File -> Local Library):
            // the destination bank's Local Library copy has never been confirmed against the
            // Kronos (no digest baseline yet, or the Kronos didn't answer the last time one was
            // requested) - see LibrarianShellViewModel.ConfirmDestinationBankAsync.
            public const string ConfirmStaleBankTitle = "Local Library may be out of sync";
            public static string ConfirmStaleBank(string bankLabel) =>
                $"{bankLabel} in Local Library has never been confirmed against the Kronos this session " +
                $"(no successful Sync has checked it yet).\n\n" +
                $"If it changed on the instrument - a front-panel edit, or a write from elsewhere - placing here " +
                $"bases the edit on a copy that may already be stale, and Sync's own conflict check only catches " +
                $"this at push time, after the placement is already made.\n\n" +
                $"Run Sync Library first to be sure, or place anyway?";
            public const string PlacementCancelledOutOfSync =
                "Cancelled - destination bank not confirmed in sync with the Kronos.";

            // ── Drop-target status hints ──
            public const string DropNotRecognizedLibraryObject = "Drop didn't carry a recognized library object.";
            public const string DropNotRecognizedMergeObject   = "Drop didn't carry a recognized Merge Window object.";
            public const string DropOutsideRow =
                "Drop landed outside any bank/slot row - try dropping directly on one.";
            public const string DropOntoBankOrSlot = "Drop onto a specific bank or slot.";
            public const string DropOntoSpecificSlot = "Drop directly onto a specific slot - pick exactly where this lands.";
            public const string DropOntoSlotOrBankForGroup =
                "Drop onto a specific slot or bank so the group has somewhere to land.";
            public const string DragMoveOneAtATime =
                "Drag-move works one item at a time - select a single item, or hold Ctrl to copy.";
            public const string SelectSlotOrBankToPasteInto = "Select a slot or bank to paste into.";
            public const string BankTypeChangeCancelled = "Bank type change cancelled.";
            public static string PlacedAt(string what, string where) => $"Placed {what} at {where}";
            public static string PlacedAtWhere(string where)         => $"Placed at {where}";
            public static string PlaceFailedDetail(string? error)    => $"Place failed: {error}";
            // Duplicate-content guard (Merge -> Local): content byte-identical to something
            // already elsewhere in Local Library is reused instead of written a second time.
            public static string ReusedExistingContent(string existingWhere) =>
                $"Identical content already at {existingWhere} - reused it instead of copying";
            public static string ReusedExistingContentCount(int count) =>
                $"{count} item(s) already existed elsewhere - reused instead of copying";

            // Shown in the dependency lists for a reference into a read-only ROM Program bank
            // (GM, g(1)-g(9), g(d)) - those live on the instrument itself, so they're never a gap
            // and never something to place. See ObjectReferenceWalker.IsAlwaysAvailable.
            public const string RomBankAlwaysAvailable = "ROM bank, always available on the Kronos";

            // A reference that resolves to an INIT/placeholder Program - satisfied, but not really
            // the sound the referrer wants. See ProgramBody.IsInit.
            public const string InitPlaceholderSuffix = "(INIT placeholder)";

            // A reference the loaded PCG / Merge Window can't satisfy but Local Library CAN, since
            // the reference is an address and Local Library already holds that address. Says where
            // it was looked for AND where it was found, so the row can't be misread as either a
            // gap or as having come out of the source being browsed.
            public static string ResolvedFromLocalLibrary(string whereMissing) =>
                $"(not {whereMissing}; already in your Local Library at this address)";

            // ── Properties dialog: dependency lists + "Scan PCG..." ──
            public const string DependenciesHeader   = "Dependencies";
            public const string RequiresHeader       = "Requires (what this object needs)";
            public const string UsedByHeader         = "Used by (what needs this object)";
            public const string RequiresNothing      = "Nothing - this object references no others.";
            public const string UsedByNothing        = "Nothing currently references this object.";
            public const string ScanPcgButton        = "Scan PCG for missing...";
            public const string ScanPcgDialogTitle   = "Scan a PCG for missing dependencies";
            public const string ScanNothingMissing   = "Nothing missing - every dependency already resolves.";
            public static string ScanFoundInPcg(int found, int missing, string fileName) =>
                $"Found {found} of {missing} in {fileName} - staged in the Merge Window.";
            public static string ScanFoundNoneInPcg(int missing, string fileName) =>
                $"{fileName} has none of the {missing} missing dependency(ies) - try another PCG.";
            public static string ScanFailed(string detail) => $"Scan failed: {detail}";
            public static string UndoScannedPcgForDependencies(string fileName) =>
                $"Staged dependencies found in {fileName}";

            // ── Undo (Ctrl+Z - see Core/LocalLibrary/LibrarianUndo.cs) ──
            // Step descriptions are past-tense phrases naming the action, so they read correctly in
            // BOTH surroundings they appear in: the button's "Undo: <desc> (Ctrl+Z)" tooltip and the
            // status line's "Undone: <desc>".
            public const string UndoNothingTooltip = "Nothing to undo (Ctrl+Z)";
            public static string UndoTooltip(string description) => $"Undo: {description} (Ctrl+Z)";
            public const string NothingToUndo = "Nothing to undo.";
            public static string Undone(string description) => $"Undone: {description}";

            public static string UndoPlacedAt(string what, string where)   => $"Placed {what} at {where}";
            public static string UndoPlacedMergeItemAt(string where)       => $"Placed a Merge Window item at {where}";
            public static string UndoPlacedGroup(int count, string bank)   => $"Placed {count} item(s) into {bank}";
            public static string UndoAutoFilled(int count)                 => $"Auto-Filled {count} staged item(s) into Local Library";
            public static string UndoCopiedBankWithTypeChange(string bank) => $"Copied a whole bank into {bank} with a type change";
            public static string UndoPulledIntoMerge(int count)            => $"Pulled {count} item(s) into the Merge Window";
            public static string UndoRemovedFromMerge(int count)           => $"Removed {count} item(s) from the Merge Window";
            public const string UndoClearedMerge                            = "Cleared the Merge Window";
            public static string UndoPastedAt(string where)                => $"Pasted into {where}";
            public static string UndoRenamed(string what)                  => $"Renamed {what}";
            public static string UndoEdited(string what)                   => $"Edited {what}";
            public static string UndoEditedSlot(string what, int slot)     => $"Edited {what} slot {slot}";
            public static string UndoDiscarded(string what)                => $"Discarded {what}";
            public static string UndoDiscardedMany(int count)              => $"Discarded {count} item(s)";
            public static string UndoDeletedOrRestored(bool deleted, string what) =>
                $"{(deleted ? "Deleted" : "Restored")} {what}";
            public static string UndoDeletedOrRestoredMany(bool deleted, int count) =>
                $"{(deleted ? "Deleted" : "Restored")} {count} item(s)";
            public static string UndoClearedChanges(int count)             => $"Cleared {count} pending change(s)";
        }

        /// <summary>Pull/push pipeline (Core) - progress strings, plus the plan warnings the
        /// push gate raises. Severity is carried by PlanWarning, not by the message text.</summary>
        public static class Sync
        {
            public static string BulkDumping(string display, string bankLabel) =>
                $"Bulk-dumping {display} {bankLabel}...";
            public static string Pulling(int done, int total, string display, string bankLabel, int number) =>
                $"Pulling {done}/{total} - {display} {bankLabel}:{number:D3}";

            public static PlanWarning RefusePendingDependencies(int count) => PlanWarning.Refuse(
                $"{count} dependency(ies) still pending in the session clipboard - place them before pushing");
            public static PlanWarning RefuseMissingReference(string loc, string missingRef, object kind) => PlanWarning.Refuse(
                $"{loc} references {missingRef} ({kind}), which does not exist locally");
            public static PlanWarning RefuseBankTypeMismatch(string bankLabel, string bankType) => PlanWarning.Refuse(
                $"{bankLabel} is an {bankType} bank, " +
                $"but the pending Program(s) are not. Copy the whole bank with a type change " +
                $"(drag the bank onto it), or place them in a matching bank.");

            public static readonly PlanWarning CheckNothingToPush =
                PlanWarning.Check("nothing to push - no local changes are pending");
            // The window closed (LibrarianShellViewModel.Dispose cancelling its sync token)
            // partway through the pull half - the push half never started, so nothing was
            // written. Only ever reaches AppLog; nothing renders WarningText once the window
            // that owned it is gone.
            public static readonly PlanWarning CheckSyncCancelled =
                PlanWarning.Check("sync cancelled - the Librarian window closed before it finished");
            public static readonly PlanWarning CheckEveryChangeConflicted =
                PlanWarning.Check("every pending change conflicted or was rejected - nothing left to push");
        }

        /// <summary>Move/placement planner gate reasons (BatchMoveModel / LibrarianModel PlanMove).
        /// Surfaced as pane status when a drag/paste is refused; severity is carried by PlanWarning.
        /// These are deliberately technical (they cite HD-1/EXi bank types, wire byte sizes, and
        /// func reply codes) because that detail is what tells the user how to fix the refusal.</summary>
        public static class Move
        {
            public static readonly PlanWarning NoPlacements = PlanWarning.Refuse("no placements to perform");
            public static PlanWarning DuplicateDestination(string label, int count) => PlanWarning.Refuse(
                $"duplicate destination {label} targeted by {count} placement(s)");
            public static readonly PlanWarning BatchTypeMismatch = PlanWarning.Refuse(
                "batch contains an object of a different type than the batch's object type");
            public static readonly PlanWarning DestinationReadOnly = PlanWarning.Refuse("a destination bank is read-only (GM/g)");
            public static PlanWarning BankTypesDiffer(string fromLabel, string fromType, string toLabel, string toType) => PlanWarning.Refuse(
                $"{fromLabel} ({fromType}) cannot move to {toLabel} ({toType}) - bank types differ");
            public static PlanWarning CheckCrossBankUnverified(string fromLabel, string toLabel) => PlanWarning.Check(
                $"{fromLabel} -> {toLabel} crosses banks whose HD-1/EXi type couldn't be fully verified - the write may be rejected (Reply 64).");
            public static PlanWarning WrongFormatForBank(string toLabel, string bankType, int expectedLen, string sourceLabel, int actualLen) => PlanWarning.Refuse(
                $"{toLabel} is a {bankType} bank ({expectedLen}-byte Programs), but {sourceLabel} is {actualLen} bytes - wrong format for this bank.");
            public static PlanWarning CheckDestTypeUnverified(string toLabel) => PlanWarning.Check(
                $"{toLabel}'s HD-1/EXi type couldn't be fully verified - the write may be rejected (Reply 64).");
            public static PlanWarning AlreadyContainsExact(string toLabel) => PlanWarning.Refuse(
                $"{toLabel} already contains this exact object - nothing to place.");
            public static PlanWarning ReferencedWouldBeOverwritten(string toLabel, int refCount) => PlanWarning.Refuse(
                $"{toLabel} is referenced by {refCount} object(s) and would be overwritten without being relocated itself - add it to this batch as a source, or choose a different destination.");
            public static PlanWarning InitOccupantOverwritten(string toLabel, int refCount) => PlanWarning.Check(
                $"{toLabel} held an INIT placeholder referenced by {refCount} object(s) - placed anyway (an INIT slot is a placeholder, not data), so those referrer(s) now resolve to the new object.");
            public static PlanWarning ForcedOverwriteReferenced(string toLabel, int refCount) => PlanWarning.Check(
                $"{toLabel} was referenced by {refCount} object(s) - Force Overwrite placed it anyway, so those referrer(s) now resolve to the NEW object instead of the old one.");
            public static PlanWarning CheckOverwrittenNotDiverted(string toLabel) => PlanWarning.Check(
                $"{toLabel} is overwritten and not diverted - its prior contents are only recoverable from the automatic backup.");
            public static PlanWarning ReferringObjectMissing(int refObj, int refBank, int refIndex) => PlanWarning.Refuse(
                $"referring object missing from catalog (obj {refObj:X2} bank {refBank:X2} idx {refIndex}) - re-scan before moving");

            public static readonly PlanWarning CannotMoveBetweenTypes = PlanWarning.Refuse(
                "cannot move between different object types (program vs combi)");
            public static PlanWarning DestinationReadOnlyBank(string dstLabel) => PlanWarning.Refuse(
                $"destination {dstLabel} is a read-only (GM/g) program bank");
            public static readonly PlanWarning SameLocation = PlanWarning.Refuse("source and destination are the same location");
            public static readonly PlanWarning CheckProgramMoveAcrossBanks = PlanWarning.Check(
                "program move across banks - destination bank must be the same type (HD-1/EXi) or the write is rejected (Reply 64).");
        }
    }

    /// <summary>Unresolved-dependencies gate dialog (UnresolvedDependenciesDialog).</summary>
    public static class UnresolvedDependencies
    {
        // Says plainly WHAT is wrong and WHERE the address lives, because the old wording didn't:
        // "I-C:008 - needed by 1 object" left it ambiguous whether that address was a Program or a
        // Combi, whether it referred to something in the loaded PCG or in Local Library, and what
        // the user was supposed to do about it other than pick one of two buttons.
        public static string Heading(int count) =>
            $"{count} reference{(count == 1 ? "" : "s")} below point at an object that isn't in your Local Library.\n\n" +
            "These are addresses INSIDE the Combis/Set Lists you're about to push: each one names a slot the " +
            "Kronos will look in, which is currently empty (or holds something else). Those objects will load, " +
            "but the listed timbres/slots will sound wrong.\n\n" +
            "Right-click any row to see what needs it, or to search a .pcg file for the missing object - " +
            "anything found is staged in the Merge Window, and placing it anywhere repoints the reference " +
            "automatically at the next Sync/Commit.";

        // Every gap in the list was located and staged. Deliberately does NOT say "resolved":
        // staging is not placement, and the references are only repointed once the staged objects
        // are placed and the next Sync/Commit runs.
        public const string AllLocated =
            "Every missing object below has been found and staged in the Merge Window.\n\n" +
            "They aren't placed yet - drop them into Local Library, and the references that " +
            "needed them are repointed automatically at the next Sync/Commit.";

        // Type name first ("Program I-C:008", never a bare "I-C:008" - Program and Combi bank
        // labels look identical), then who needs it and through which site.
        public static string Row(string typeName, string label, string name, int count) =>
            $"{typeName} {label}{(string.IsNullOrEmpty(name) ? "" : $"  “{name}”")} - needed by {count} object{(count == 1 ? "" : "s")}";

        public static string RowReferrer(string typeName, string label, string refKind) =>
            $"        -> {typeName} {label} ({refKind})";
        public static string RowReferrerMore(int more) => $"        -> ... and {more} more";

        // Shown where the "More Info" popup would otherwise list a row's own outgoing references
        // (ObjectInfoDialog) - a gap row has no object behind it, so there is nothing to list and
        // "(references nothing)" would be an outright wrong answer.
        public const string NotStagedChildren =
            "Not staged - what this object itself references can't be known until it's found.";

        public const string ScanMenuItem   = "Search a PCG for this object...";
        public const string CopyMenuItem   = "Copy all details";
        public const string CopiedToClipboard = "Details copied to the clipboard.";
        public static string ScanFound(string label, string fileName) =>
            $"Found {label} in {fileName} - staged in the Merge Window. Place it anywhere.";
        // The sweep found several at once - the file the user picked for one gap turned out to
        // hold others too, which is the common case when they were all saved together.
        public static string ScanFoundMany(int found, string fileName) =>
            $"Found {found} of the missing objects in {fileName} - staged in the Merge Window. Place them anywhere.";
        public static string ScanNotFound(string label, string fileName) =>
            $"{fileName} doesn't contain {label}, or any of the others still listed - try another .pcg file.";
        public static string ScanFailed(string detail) => $"Search failed: {detail}";
    }

    /// <summary>Remote file picker (RemoteFilePickerDialog) status line.</summary>
    public static class RemoteFilePicker
    {
        public const string Connecting = "Connecting...";
        public static string ConnectFailed(string detail) => $"Connect failed: {detail}";
        public const string Loading = "Loading...";
        public static string ItemCount(int count) => $"{count} item(s)";
        public static string Error(string detail) => $"Error: {detail}";
        public static string Downloading(string name) => $"Downloading {name}...";
        public static string DownloadFailed(string detail) => $"Download failed: {detail}";
    }

    /// <summary>Remote sample picker (SampleRemoteBrowserDialog) status line.</summary>
    public static class RemoteSamplePicker
    {
        public const string Connecting = "Connecting...";
        public static string ConnectFailed(string detail) => $"Connect failed: {detail}";
        public const string Loading = "Loading...";
        public static string ItemCount(int count) => $"{count} item(s)";
        public static string Error(string detail) => $"Error: {detail}";
        public static string Downloading(string name) => $"Downloading {name}...";
        public static string PullingClosure(string name) => $"Downloading {name} and its referenced files...";
        public static string DownloadFailed(string detail) => $"Download failed: {detail}";
    }

    /// <summary>Input tester tool (InputTesterWindow) - field prompts and status feedback.</summary>
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
        public static string Loaded(int count, string path) => $"Loaded {count} entries - {path}";
        public static string TestedProgress(int tested, int total) => $"{tested} / {total} tested";
    }

    /// <summary>Macro editor step hints (SettingsWindow).</summary>
    public static class Macro
    {
        public const string Recording       = "(recording - press keys...)";
        public const string NoStepsRecorded = "(no steps recorded)";
    }
}
