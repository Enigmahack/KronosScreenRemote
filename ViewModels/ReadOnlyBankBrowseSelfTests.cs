namespace KronosScreenRemote.ViewModels;

using System.IO;

// Off-hardware coverage for the read-only factory (GM/g) Program banks in the Local Library
// pane. Those banks are browse-only: shown so their content can be looked up, never written to,
// and never body-pulled - only names, read from the shared name cache via the service.
//
// This suite exists because every failure mode here is SILENT. A wrong cache key, a tree built
// before the name source was wired, or a read-only descriptor that never reports a bank as
// read-only all produce the same symptom: no GM rows, indistinguishable from an instrument whose
// names simply haven't been swept yet. The write-refusal half is the opposite risk - a bank the
// user can see is a bank a drop can land on - and is covered from the data layer up.
// Wired into App.xaml.cs's --librarian-selftest.
static class ReadOnlyBankBrowseSelfTests
{
    // Unique, never "" - the ctor's bank-type warm-up persists to a host-keyed global cache, and
    // sharing a host (especially the empty one) pollutes every other VM-based self-test.
    const string Host = "selftest-readonly-browse-host";

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_readonly_browse");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            // One real writable Program so the library isn't empty (an empty library hides the
            // tree entirely behind the "Sync to get started" hint).
            exec.Seed(LibObj.Program, 0x40, 0, 5, ProgramBody.WriteName(
                new byte[ProgramFormatConverter.WireSizeHd1], "REAL PATCH"));
            // Names for GM (func-33 type 1 = program, object bank 0x10), as the shared name sweep
            // would have cached them. Deliberately SPARSE and not starting at 0: the sweep
            // converges over several sessions, so a partly-known bank is the normal state.
            exec.BankNames[(1, 0x10)] = new Dictionary<int, string> { [0] = "A.Piano 1", [7] = "E.Piano 3" };

            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), Host);

            // The tree must already carry the GM rows straight out of the ctor - nothing else
            // refreshes it before the window is shown.
            var programsRoot = vm.LocalPane.Roots.FirstOrDefault(r => r.Label == "Programs");
            Check("programs-root-present", programsRoot != null);
            if (programsRoot == null) return fails;

            var gmBank = programsRoot.Children.FirstOrDefault(c => c.BankRef is (LibObj.Program, 0x10));
            Check("gm-bank-node-present", gmBank != null);
            if (gmBank == null) return fails;

            Check("gm-bank-marked-readonly", gmBank.IsReadOnly);
            Check("gm-bank-label-says-readonly", gmBank.Label.Contains("read-only"));
            // Only the slots actually known are rows - not 128 blanks.
            Check("gm-shows-only-known-slots", gmBank.Children.Count == 2);
            Check("gm-slots-carry-names",
                gmBank.Children.Any(c => c.Label.Contains("A.Piano 1")) &&
                gmBank.Children.Any(c => c.Label.Contains("E.Piano 3")));
            Check("gm-slots-marked-readonly", gmBank.Children.All(c => c.IsReadOnly));
            // A writable bank in the same tree must NOT be marked read-only - the flag has to
            // discriminate, not just be set everywhere.
            var userBank = programsRoot.Children.FirstOrDefault(c => c.BankRef is (LibObj.Program, 0x40));
            Check("writable-bank-not-readonly", userBank is { IsReadOnly: false });

            // Bodies are never pulled for a read-only bank: the row exists, the library does not.
            Check("gm-body-never-cached", !cache.Exists(LibObj.Program, 0x10, 0));

            // ── Never a destination ────────────────────────────────────────────────────────
            var gmLoc = new ObjLoc(LibObj.Program, 0x10, 0);
            vm.LocalPane.Copy(new[] { new ObjLoc(LibObj.Program, 0x40, 0) });
            var (slotOk, slotMsg) = vm.LocalPane.PasteIntoSlot(gmLoc);
            Check("paste-into-gm-slot-refused", !slotOk && slotMsg != null && slotMsg.Contains("read-only"));
            var (bankOk, bankMsg) = vm.LocalPane.PasteIntoBank(LibObj.Program, 0x10);
            Check("paste-into-gm-bank-refused", !bankOk && bankMsg != null && bankMsg.Contains("read-only"));
            Check("gm-still-empty-after-refused-pastes", !cache.Exists(LibObj.Program, 0x10, 0));

            // A header drop auto-fill must never resolve TO a read-only bank, however much room
            // one appears to have (they are all "empty" as far as the cache is concerned).
            Check("typeroot-paste-never-resolves-to-gm",
                vm.LocalPane.FindBankForPaste(LibObj.Program) is int b && !KronosBanks.IsReadOnlyProgramBank(b));

            // ── Never the SUBJECT of an action either ──────────────────────────────────────
            // A GM row has a real Loc (it labels and selects like any other row), so it can be
            // handed to any action that takes one - and there is no body behind it.
            vm.PullLocalIntoMerge(gmLoc);
            Check("gm-not-stageable-into-merge", vm.MergePane.Roots.Count == 0);
            vm.LocalPane.ClearClipboard();   // else HasClipboard still reflects the valid copy above
            vm.LocalPane.Copy(new[] { gmLoc });
            Check("gm-not-copyable", !vm.LocalPane.HasClipboard);
            vm.LocalPane.ToggleDelete(gmLoc);
            Check("gm-delete-explains-readonly", vm.LocalPane.StatusText.Contains("read-only"));

            vm.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return fails;
    }
}
