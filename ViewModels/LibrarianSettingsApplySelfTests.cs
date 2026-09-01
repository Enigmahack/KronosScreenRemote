namespace KronosScreenRemote.ViewModels;

using System.IO;

// Off-hardware coverage for LibrarianShellViewModel.ApplySettings - what happens when the user
// changes Settings while the Librarian window is already open.
//
// This exists because MainWindow.ApplySettingsResult REPLACES its AppSettings instance rather
// than mutating it, and an open Librarian keeps its own reference to the old one. Both failures
// below were silent: the destructive-write toggle never reaching the push (including turning it
// OFF), and the Merge Window's duplicate toggles writing through to the pre-dialog snapshot and
// then persisting it back over settings.json - reverting everything the dialog had just changed.
// Wired into App.xaml.cs's --librarian-selftest.
static class LibrarianSettingsApplySelfTests
{
    // Unique, never "" - the ctor's bank-type warm-up persists to a host-keyed global cache.
    const string Host = "selftest-settings-apply-host";

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_settings_apply");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            exec.Seed(LibObj.Program, 0x40, 0, 5, ProgramBody.WriteName(
                new byte[ProgramFormatConverter.WireSizeHd1], "REAL PATCH"));
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);

            var opened = new AppSettings
            {
                LibrarianForceDestructiveWrite = true,
                MergePreserveDuplicatePrograms = true,
                KronosHost = "opened-with",
            };
            var vm = new LibrarianShellViewModel(exec, cache, opened, Host);

            // Never the real Storage.SaveSettings: this must not touch settings.json beside the exe.
            AppSettings? persisted = null;
            vm.PersistSettings = s => persisted = s;

            Check("seeded-destructive-from-settings", vm.ForceDestructiveWrite);
            Check("seeded-dup-programs-from-settings", vm.MergePreserveDuplicatePrograms);

            // The user opens Settings, turns destructive write OFF, and changes something else.
            var applied = new AppSettings
            {
                LibrarianForceDestructiveWrite = false,
                MergePreserveDuplicatePrograms = true,
                KronosHost = "changed-in-dialog",
            };
            vm.ApplySettings(applied);
            Check("destructive-write-turns-off-live", !vm.ForceDestructiveWrite);

            // The bug: this toolbar flip used to write through to `opened` and persist THAT,
            // silently reverting KronosHost (and every other field the dialog had just changed)
            // back to the pre-dialog snapshot.
            vm.MergePreserveDuplicatePrograms = false;
            Check("toolbar-flip-persists-something", persisted != null);
            Check("toolbar-flip-persists-post-dialog-settings", persisted?.KronosHost == "changed-in-dialog");
            Check("toolbar-flip-does-not-mutate-old-instance", opened.MergePreserveDuplicatePrograms);
            Check("toolbar-flip-recorded-on-new-instance", !applied.MergePreserveDuplicatePrograms);

            // Turning it back ON is what actually arms the push, so pin both directions.
            vm.ApplySettings(new AppSettings { LibrarianForceDestructiveWrite = true });
            Check("destructive-write-turns-on-live", vm.ForceDestructiveWrite);

            vm.Dispose();
        }
        finally
        {
            // Retried, not a plain Delete - same reason ReadOnlyBankBrowseSelfTests documents:
            // the ViewModel starts background blob readers nothing here can await.
            for (int attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }

        return fails;
    }
}
