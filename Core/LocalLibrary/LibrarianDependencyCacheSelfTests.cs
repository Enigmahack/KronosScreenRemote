namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware self-test for the "Object Dependencies" panel's per-object walk memo
// (LibrarianShellViewModel._walkCache). Populating that panel walks the selection's
// dependencies transitively, and every step used to re-read the referenced body off the CAS
// store - two filesystem round trips each, over a DataDir that is routinely an SMB share - so
// clicking one Combi cost a body read per referenced Program, every single time.
//
// The assertions that matter here are the MISS-COUNT ones, not the row ones: a cache that is
// silently missed on every lookup (a key mismatch, an empty content hash, a helper that reads
// the body before consulting the dictionary) produces byte-identical rows and would sail
// through any purely correctness-based check while the stall stayed exactly as it was.
static class LibrarianDependencyCacheSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_dependency_cache");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();

            // One Combi whose 16 timbres each reference a DIFFERENT Program, so the transitive
            // walk really does visit 17 distinct bodies rather than deduping down to two.
            int fb = KronosBanks.ObjBankToFunc33(1, 0x40);
            for (int t = 0; t < LibRefs.TimbreCount; t++)
            {
                var progBody = new byte[ProgramFormatConverter.WireSizeHd1];
                progBody[100] = (byte)(t + 1);   // distinct bodies -> distinct content hashes
                exec.Seed(LibObj.Program, 0x40, t, 5, progBody);
            }
            var combiBody = new byte[7810];
            for (int t = 0; t < LibRefs.TimbreCount; t++) LibRefs.SetCombiTimbreRef(combiBody, t, fb, t);
            exec.Seed(LibObj.Combi, 0x00, 0, 3, combiBody);

            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);

            // A UNIQUE host key - see DependencyResolutionSelfTests' own comment on why sharing
            // one with another self-test loads that test's persisted Program bank types.
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-depcache-host");
            var combiLoc = new ObjLoc(LibObj.Combi, 0x00, 0);
            var selection = new[] { combiLoc };

            // ── First population: cold, so every distinct body is read exactly once ──
            // ObjectDependencyRows is the SELECTION's rows plus any missing-dependency rows the
            // Merge Window contributes (LibrarianShellViewModel.BuildMergeGapRows). The row-count
            // checks below only hold because nothing is staged here; stage something in this test
            // and they must subtract the gap rows first.
            vm.ShowLocalObjectDependencies(selection);
            int coldMisses = vm.WalkCacheMisses;
            int coldRows = vm.ObjectDependencyRows.Count;
            Check("cold-population-produced-rows", coldRows > 0);
            Check("cold-population-walked-combi-plus-16-programs", coldMisses == 17);

            // ── Repeat population: identical rows, and NOT ONE further body read ──
            vm.ShowLocalObjectDependencies(selection);
            Check("repeat-population-is-all-cache-hits", vm.WalkCacheMisses == coldMisses);
            Check("repeat-population-same-row-count", vm.ObjectDependencyRows.Count == coldRows);

            // ── An edit invalidates ONLY the object edited ──
            // Renaming writes the name into the body, so that Program's content hash changes and
            // its entry misses; the Combi and the other 15 Programs are untouched and stay warm.
            var renamed = new ObjLoc(LibObj.Program, 0x40, 3);
            LocalEditOps.Rename(cache, renamed, "CACHE-BUST", DateTime.UtcNow);
            vm.ShowLocalObjectDependencies(selection);
            Check("edit-invalidates-exactly-one-object", vm.WalkCacheMisses == coldMisses + 1);
            Check("edit-keeps-row-count", vm.ObjectDependencyRows.Count == coldRows);

            // And the re-walk is itself cached from then on.
            vm.ShowLocalObjectDependencies(selection);
            Check("post-edit-population-is-all-cache-hits", vm.WalkCacheMisses == coldMisses + 1);

            // ── The memo must never outvote LIVE index state ──
            // Whether a reference is present is read fresh on every population (_cache.Exists),
            // never from the memo - otherwise a dependency that turns up later would keep
            // reporting as missing. Placing a Program the Combi references but that was never
            // pulled must change the rows even though NO cached body changed.
            var gapCombi = new byte[7810];
            for (int t = 0; t < LibRefs.TimbreCount; t++)
                LibRefs.SetCombiTimbreRef(gapCombi, t, fb, 100 + t);   // 0x40:100.. - empty slots in a seeded bank
            var (gapOk, gapErr, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x00, 1), LibObj.Combi, 3,
                gapCombi, "gap-combi", divertDisplacedToClipboard: false, DateTime.UtcNow);
            Check("gap-combi-placed", gapOk && gapErr == null);
            var gapLoc = new[] { new ObjLoc(LibObj.Combi, 0x00, 1) };

            vm.ShowLocalObjectDependencies(gapLoc);
            string beforeFill = string.Join("|", vm.ObjectDependencyRows.Select(r => r.Description));
            Check("gap-combi-reports-missing-dependencies", beforeFill.Length > 0);

            // forceOverwrite because the orphan gate otherwise refuses to write a slot the gap
            // Combi references - which is exactly the slot being filled here on purpose.
            var (fillOk, fillErr, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, 0x40, 100), LibObj.Program, 5,
                new byte[ProgramFormatConverter.WireSizeHd1], "filled-dep", divertDisplacedToClipboard: false, DateTime.UtcNow,
                bankTypeOf: null, forceOverwrite: true);
            Check("dependency-program-placed", fillOk && fillErr == null);
            Check("dependency-program-now-exists", cache.Exists(LibObj.Program, 0x40, 100));
            vm.ShowLocalObjectDependencies(gapLoc);
            string afterFill = string.Join("|", vm.ObjectDependencyRows.Select(r => r.Description));
            Check("placing-a-dependency-updates-rows-without-any-flush", afterFill != beforeFill);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return fails;
    }
}
