namespace KronosScreenRemote;

using System.IO;
using System.Text;

// Off-hardware self-test for the Librarian's Merge Window (Core/LocalLibrary/MergeCache.cs +
// MergeCachePersistence.cs) - fully headless, no UI or hardware involved, matching every other
// pure/sync self-test in this file's neighborhood. Exercises: transitive auto-pull across a
// Combi->Program and Set List->Combi->Program chain, batch-scoped byte-identical dedup and its
// "shared by multiple referrers" bookkeeping, gap tracking + later reconciliation from a second
// PCG, the many-to-one referrer patch at placement time, move-on-place (Remove) semantics,
// Clear, and the two persistence strategies (including switching between them mid-session).
static class MergeCacheSelfTests
{
    static string ScratchRoot => Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_cache");

    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = ScratchRoot;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            // ── Fixtures ──────────────────────────────────────────────────────────────
            // PCG A: Program A; Combi X and Combi Y both reference Program A (dedup + shared
            // marker); Combi Z references a Program NOT in this PCG (a gap); Set List S
            // references Combi X (exercises the transitive Set List -> Combi -> Program pull).
            // Program A lives in I-B, NOT I-A. Deliberate: func-33 bank 0 / number 0 is the zero
            // default every timbre of an INIT Combi already holds, so a Combi whose only reference
            // is (0, 0) satisfies CombiBody.AllTimbresAtDefault and reads as an init placeholder
            // that InitObjects correctly reports as having NO dependencies. Combi X/Y would then
            // stage alone, Program A would never appear, and every progAEntry! below would throw.
            int fbProg0 = KronosBanks.ObjBankToFunc33(1, 0x01);     // Program bank 0x01 (I-B) -> func33
            int fbCombi0 = KronosBanks.ObjBankToFunc33(0, 0x00);    // Combi bank 0x00 -> func33
            int fbProgMissing = KronosBanks.ObjBankToFunc33(1, 0x40);   // Program bank 0x40 -> func33

            var pcgABytes = BuildSyntheticPcg(fbProg0, fbCombi0, fbProgMissing);
            var fileA = PcgFile.Open(pcgABytes);
            Check("pcgA-opens", fileA != null);
            if (fileA == null) return fails;
            var pcgA = new PcgLibraryView(fileA);

            var progALoc = new ObjLoc(LibObj.Program, 0x01, 0);
            var combiXLoc = new ObjLoc(LibObj.Combi, 0x00, 0);
            var combiYLoc = new ObjLoc(LibObj.Combi, 0x00, 1);
            var combiZLoc = new ObjLoc(LibObj.Combi, 0x00, 2);
            var setListLoc = new ObjLoc(LibObj.SetList, 0, 0);
            var progMissingLoc = new ObjLoc(LibObj.Program, 0x40, 2);   // what Combi Z's timbre expects

            // ── Transitive auto-pull: Set List -> Combi -> Program ─────────────────────
            var cache = new MergeCache(new InMemoryMergeCachePersistence());
            var (added1, gaps1) = cache.PullFromPcg(pcgA, "pcgA.pcg", setListLoc);
            Check("transitive-adds-3", added1.Count == 3);   // Set List S, Combi X, Program A
            Check("transitive-no-gaps", gaps1.Count == 0);

            var setListEntry = cache.Entries.FirstOrDefault(e => e.ObjType == LibObj.SetList);
            var combiXEntry = cache.Entries.FirstOrDefault(e => e.DisplayName == "COMBI X");
            var progAEntry = cache.Entries.FirstOrDefault(e => e.DisplayName == "PROG A");
            Check("transitive-setlist-present", setListEntry != null);
            Check("transitive-combiX-present", combiXEntry != null);
            Check("transitive-progA-present", progAEntry != null);
            Check("transitive-combiX-is-dependency-not-toplevel", combiXEntry != null && !combiXEntry.IsTopLevelPull);
            Check("transitive-setlist-is-toplevel", setListEntry != null && setListEntry.IsTopLevelPull);
            Check("transitive-progA-referenced-by-combiX", progAEntry != null && combiXEntry != null &&
                progAEntry.ReferencedBy.Contains(combiXEntry.ContentHash));

            // ── Batch dedup + shared marker: pulling Combi Y (also -> Program A) must NOT
            // duplicate Program A, and must add Combi X's sibling to Program A's referrers ──
            var (added2, gaps2) = cache.PullFromPcg(pcgA, "pcgA.pcg", combiYLoc);
            Check("dedup-combiY-adds-1", added2.Count == 1);   // only Combi Y itself is new
            Check("dedup-no-gaps", gaps2.Count == 0);
            var combiYEntry = cache.Entries.FirstOrDefault(e => e.DisplayName == "COMBI Y");
            Check("dedup-combiY-present", combiYEntry != null);
            Check("dedup-progA-still-one-entry", cache.Entries.Count(e => e.DisplayName == "PROG A") == 1);
            Check("dedup-progA-shared-by-both", progAEntry != null && combiXEntry != null && combiYEntry != null &&
                progAEntry.ReferencedBy.Count == 2 &&
                progAEntry.ReferencedBy.Contains(combiXEntry.ContentHash) && progAEntry.ReferencedBy.Contains(combiYEntry.ContentHash));

            // Re-pulling the EXACT same top-level object again must not create a duplicate.
            int countBeforeRepull = cache.Entries.Count;
            var (added3, _) = cache.PullFromPcg(pcgA, "pcgA.pcg", combiYLoc);
            Check("repull-toplevel-no-duplicate", added3.Count == 0 && cache.Entries.Count == countBeforeRepull);

            // ── Gap tracking: Combi Z's dependency isn't in PCG A ───────────────────────
            var (added4, gaps4) = cache.PullFromPcg(pcgA, "pcgA.pcg", combiZLoc);
            Check("gap-combiZ-added", added4.Count == 1);
            Check("gap-reported", gaps4.Count == 1 && gaps4[0].MissingRef == progMissingLoc);
            var combiZEntry = cache.Entries.FirstOrDefault(e => e.DisplayName == "COMBI Z");
            Check("gap-combiZ-has-unresolved", combiZEntry != null && combiZEntry.HasUnresolvedDependencies);

            // ── Gap reconciliation: a SECOND PCG happens to have exactly that Program ───
            var pcgBBytes = BuildSyntheticPcgWithProgram(fbProgMissing, "PROG MISSING", progMissingLoc.Number);
            var fileB = PcgFile.Open(pcgBBytes);
            Check("pcgB-opens", fileB != null);
            if (fileB == null) return fails;
            var pcgB = new PcgLibraryView(fileB);

            var (added5, gaps5) = cache.PullFromPcg(pcgB, "pcgB.pcg", progMissingLoc);
            Check("reconcile-adds-1", added5.Count == 1);
            Check("reconcile-no-new-gaps", gaps5.Count == 0);
            var progMissingEntry = cache.Entries.FirstOrDefault(e => e.DisplayName == "PROG MISSING");
            Check("reconcile-progMissing-present", progMissingEntry != null);
            Check("reconcile-combiZ-now-resolved", combiZEntry != null && !combiZEntry.HasUnresolvedDependencies);
            Check("reconcile-progMissing-referenced-by-combiZ", progMissingEntry != null && combiZEntry != null &&
                progMissingEntry.ReferencedBy.Contains(combiZEntry.ContentHash));

            // ── Placement: many-to-one referrer patch ───────────────────────────────────
            // Program A is shared by Combi X and Combi Y - placing it ONCE must cause BOTH
            // referrers' patched bodies to point at that SAME destination.
            var progADest = new ObjLoc(LibObj.Program, 0x41, 7);
            cache.RecordPlacement(progAEntry!.ContentHash, progADest);
            int fbDest = KronosBanks.ObjBankToFunc33(1, progADest.Bank);

            var (combiXPatched, combiXUnresolved) = cache.ResolveReferencesForPlacement(combiXEntry!);
            var (combiYPatched, combiYUnresolved) = cache.ResolveReferencesForPlacement(combiYEntry!);
            var (bankX, numX) = LibRefs.CombiTimbreRef(combiXPatched, 0);
            var (bankY, numY) = LibRefs.CombiTimbreRef(combiYPatched, 0);
            Check("patch-combiX-points-at-dest", bankX == fbDest && numX == progADest.Number);
            Check("patch-combiY-points-at-dest", bankY == fbDest && numY == progADest.Number);
            Check("patch-combiX-nothing-unresolved", combiXUnresolved.Count == 0);
            Check("patch-combiY-nothing-unresolved", combiYUnresolved.Count == 0);

            // Combi Z's dependency (Program Missing) was never placed and no local-library
            // lookup was supplied - its patched body must be untouched, still pointing at the
            // ORIGINAL PCG address, and reported back as unresolved so the caller can track it
            // for a later retry (LibrarianShellViewModel.TrackMergeDependencies).
            var (combiZUnpatched, combiZUnresolved) = cache.ResolveReferencesForPlacement(combiZEntry!);
            var (bankZ, numZ) = LibRefs.CombiTimbreRef(combiZUnpatched, 0);
            Check("patch-combiZ-unresolved-dep-untouched", bankZ == fbProgMissing && numZ == progMissingLoc.Number);
            Check("patch-combiZ-reported-unresolved", combiZUnresolved.Count == 1 &&
                combiZUnresolved[0].TargetLoc.Equals(progMissingLoc) && combiZUnresolved[0].ResolvedContentHash == progMissingEntry!.ContentHash);

            // ── ResolveReferencesForPlacement's localLookup - a Local Library search, not
            // just _placedAddresses. Simulated here with a plain dictionary standing in for
            // LocalLibraryCache.FindByContentHash, since this file tests MergeCache in
            // isolation from the rest of Core/LocalLibrary.
            var fakeLocalLibrary = new Dictionary<(int ObjType, string Hash), ObjLoc>
            {
                [(LibObj.Program, progMissingEntry!.ContentHash)] = new ObjLoc(LibObj.Program, 0x43, 12),
            };
            ObjLoc? FakeLocalLookup(int objType, string hash) => fakeLocalLibrary.TryGetValue((objType, hash), out var loc) ? loc : null;

            var (combiZViaLocalLookup, combiZViaLocalLookupUnresolved) = cache.ResolveReferencesForPlacement(combiZEntry!, FakeLocalLookup);
            var (bankZ2, numZ2) = LibRefs.CombiTimbreRef(combiZViaLocalLookup, 0);
            int fbLocalFound = KronosBanks.ObjBankToFunc33(1, 0x43);
            Check("localLookup-repoints-to-found-location", bankZ2 == fbLocalFound && numZ2 == 12);
            Check("localLookup-resolves-nothing-left-unresolved", combiZViaLocalLookupUnresolved.Count == 0);

            // _placedAddresses still takes priority over localLookup when both could answer -
            // Combi X's dependency (Program A) was explicitly placed above; a localLookup that
            // would (wrongly) send it somewhere else must be ignored.
            var conflictingLookup = new Dictionary<(int, string), ObjLoc> { [(LibObj.Program, progAEntry!.ContentHash)] = new ObjLoc(LibObj.Program, 0x44, 3) };
            ObjLoc? ConflictingLookup(int objType, string hash) => conflictingLookup.TryGetValue((objType, hash), out var loc) ? loc : null;
            var (combiXViaBoth, _) = cache.ResolveReferencesForPlacement(combiXEntry!, ConflictingLookup);
            var (bankX2, numX2) = LibRefs.CombiTimbreRef(combiXViaBoth, 0);
            Check("placedAddresses-takes-priority-over-localLookup", bankX2 == fbDest && numX2 == progADest.Number);

            // ── Requirement 3: PullFromLocal - the same transitive/dedup/gap pull, sourced
            // from Local Library instead of a PCG. Seed a tiny local cache (a Combi referencing
            // a Program) via RecordPullBaselines and pull the Combi in; both must stage, the
            // referrer bookkeeping must be wired, and the origin must be labeled Local Library. ─
            string localRoot = Path.Combine(root, "local_lib");
            var localCache = new LocalLibraryCache(localRoot);
            // I-B again, and for the same reason as fbProg0 above: a lone (0, 0) timbre makes the
            // referrer read as an INIT Combi with no dependencies at all, so "local-pull-adds-2"
            // would only ever see the Combi.
            int fbLocalProg = KronosBanks.ObjBankToFunc33(1, 0x01);
            var localProgBody = new byte[3706];
            Encoding.ASCII.GetBytes("LOCAL PROG").CopyTo(localProgBody, 0);
            var localCombiBody = new byte[7810];
            Encoding.ASCII.GetBytes("LOCAL COMBI").CopyTo(localCombiBody, 0);
            SetAllTimbres(localCombiBody, fbLocalProg, 0);   // -> the local Program (all 16, see BuildSyntheticPcg)
            localCache.RecordPullBaselines(new[]
            {
                (LibObj.Program, 0x01, 0, (byte)5, localProgBody),
                (LibObj.Combi, 0x00, 0, (byte)3, localCombiBody),
            }, DateTime.UtcNow);

            var localMerge = new MergeCache(new InMemoryMergeCachePersistence());
            var (localAdded, localGaps) = localMerge.PullFromLocal(localCache, new ObjLoc(LibObj.Combi, 0x00, 0));
            Check("local-pull-adds-2", localAdded.Count == 2);   // Combi + its Program
            Check("local-pull-no-gaps", localGaps.Count == 0);
            var localCombiEntry = localMerge.Entries.FirstOrDefault(e => e.DisplayName == "LOCAL COMBI");
            var localProgEntry = localMerge.Entries.FirstOrDefault(e => e.DisplayName == "LOCAL PROG");
            Check("local-pull-combi-present", localCombiEntry != null);
            Check("local-pull-prog-present", localProgEntry != null);
            Check("local-pull-prog-referenced-by-combi", localProgEntry != null && localCombiEntry != null &&
                localProgEntry.ReferencedBy.Contains(localCombiEntry.ContentHash));
            Check("local-pull-origin-labeled-local", localCombiEntry != null &&
                localCombiEntry.Origins.Any(o => o.PcgFileName == MergeCache.LocalSourceLabel));

            // A dependency absent locally is tracked as a gap, same contract as the PCG path.
            // Point EVERY timbre at a Program not present locally - timbres left at their (0,0)
            // default would otherwise resolve to the local Program seeded above and get pulled
            // too, masking the gap under test.
            var orphanCombiBody = new byte[7810];
            Encoding.ASCII.GetBytes("ORPHAN COMBI").CopyTo(orphanCombiBody, 0);
            var missingLoc = new ObjLoc(LibObj.Program, 0x40, 5);
            for (int t = 0; t < LibRefs.TimbreCount; t++)
                LibRefs.SetCombiTimbreRef(orphanCombiBody, t, KronosBanks.ObjBankToFunc33(1, missingLoc.Bank), missingLoc.Number);
            localCache.RecordPullBaselines(new[] { (LibObj.Combi, 0x00, 1, (byte)3, orphanCombiBody) }, DateTime.UtcNow);
            var localMerge2 = new MergeCache(new InMemoryMergeCachePersistence());
            var (orphanAdded, orphanGaps) = localMerge2.PullFromLocal(localCache, new ObjLoc(LibObj.Combi, 0x00, 1));
            Check("local-pull-gap-adds-combi-only", orphanAdded.Count == 1);
            Check("local-pull-gap-reported", orphanGaps.Any(g => g.MissingRef.Equals(missingLoc)));

            // ── Move-on-place semantics: Remove takes exactly the requested entry out ───
            int countBeforeRemove = cache.Entries.Count;
            Check("remove-combiX-ok", cache.Remove(combiXEntry!.ContentHash));
            Check("remove-combiX-gone", cache.TryGet(combiXEntry.ContentHash) == null);
            Check("remove-only-that-one", cache.Entries.Count == countBeforeRemove - 1);
            Check("remove-progA-untouched", cache.TryGet(progAEntry!.ContentHash) != null);   // still referenced by Combi Y

            // ── Clear ────────────────────────────────────────────────────────────────
            cache.Clear();
            Check("clear-empties-cache", cache.Entries.Count == 0);

            // ── Persistence: FileMergeCachePersistence round-trip ───────────────────────
            string snapshotPath = Path.Combine(root, "merge_snapshot.json");
            var fileCache = new MergeCache(new FileMergeCachePersistence(snapshotPath));
            fileCache.PullFromPcg(pcgA, "pcgA.pcg", combiXLoc);
            fileCache.RecordPlacement(fileCache.Entries.First(e => e.DisplayName == "PROG A").ContentHash, progADest);
            int countBeforeReload = fileCache.Entries.Count;

            var reloaded = new MergeCache(new FileMergeCachePersistence(snapshotPath));
            Check("persistence-roundtrip-count", reloaded.Entries.Count == countBeforeReload);
            var reloadedCombiX = reloaded.Entries.FirstOrDefault(e => e.DisplayName == "COMBI X");
            Check("persistence-roundtrip-combiX-present", reloadedCombiX != null);
            var reloadedPatched = reloadedCombiX != null ? reloaded.ResolveReferencesForPlacement(reloadedCombiX).Body : null;
            Check("persistence-roundtrip-placement-survived",
                reloadedPatched != null && LibRefs.CombiTimbreRef(reloadedPatched, 0) == (fbDest, progADest.Number));

            // ── SetPersistence: Temp -> Local persists current state; Local -> Temp clears
            // the file but keeps what's in memory for the rest of this session ───────────
            var memCache = new MergeCache(new InMemoryMergeCachePersistence());
            memCache.PullFromPcg(pcgA, "pcgA.pcg", combiYLoc);
            string switchPath = Path.Combine(root, "switch_snapshot.json");
            memCache.SetPersistence(new FileMergeCachePersistence(switchPath), wasFileBacked: false);
            Check("switch-to-local-writes-file", File.Exists(switchPath));
            var afterSwitch = new MergeCache(new FileMergeCachePersistence(switchPath));
            Check("switch-to-local-content-present", afterSwitch.Entries.Any(e => e.DisplayName == "COMBI Y"));

            memCache.SetPersistence(new InMemoryMergeCachePersistence(), wasFileBacked: true);
            Check("switch-to-temp-deletes-file", !File.Exists(switchPath));
            Check("switch-to-temp-keeps-in-memory-content", memCache.Entries.Any(e => e.DisplayName == "COMBI Y"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        return fails;
    }

    // Points all 16 timbres of a Combi at one Program - see BuildSyntheticPcg's own comment for
    // why a fixture Combi must never be left with a mix of real and defaulted timbres.
    static void SetAllTimbres(byte[] combiBody, int func33Bank, int number)
    {
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(combiBody, t, func33Bank, number);
    }

    // Builds a synthetic .pcg buffer with Program A, Combi X/Y/Z, and a one-slot Set List -
    // same byte-level construction technique as CrossPanePlacementSelfTests.BuildSyntheticPcg.
    static byte[] BuildSyntheticPcg(int fbProg0, int fbCombi0, int fbProgMissing)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810, setListSize = 700;

        var progABody = new byte[programSize];
        Encoding.ASCII.GetBytes("PROG A").CopyTo(progABody, 0);

        // EVERY timbre is pointed at the intended target, never just timbre 0. A timbre left at
        // its (0, 0) default is not "unset" - it is a live reference to Program I-A:000, which
        // this PCG does not contain, so 15 untouched timbres would manufacture 15 phantom gaps on
        // top of whatever the test is actually measuring. (Only a Combi with ALL 16 still at the
        // default escapes that, by reading as an INIT placeholder with no dependencies at all -
        // see fbProg0 above. There is no middle ground: all defaults, or none.)
        var combiXBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI X").CopyTo(combiXBody, 0);
        SetAllTimbres(combiXBody, fbProg0, 0);   // -> Program A

        var combiYBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI Y").CopyTo(combiYBody, 0);
        SetAllTimbres(combiYBody, fbProg0, 0);   // -> Program A too (shared dependency)

        // Combi Z carries EXACTLY ONE gap, and the surrounding assertions count on that ("one
        // unresolved reference site", not "one distinct missing address" - gaps are reported per
        // SITE, so 16 timbres aimed at the same absent Program would report 16 gaps). Timbre 0 is
        // the gap; the other 15 point at Program A, which this PCG does have, so they resolve and
        // stay out of the way.
        var combiZBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI Z").CopyTo(combiZBody, 0);
        SetAllTimbres(combiZBody, fbProg0, 0);                        // 1..15 -> Program A (resolvable)
        LibRefs.SetCombiTimbreRef(combiZBody, 0, fbProgMissing, 2);   // 0 -> a Program NOT in this PCG

        var setListBody = new byte[setListSize];
        Encoding.ASCII.GetBytes("SETLIST S").CopyTo(setListBody, 0);
        setListBody = SetListBody.WriteSlotName(setListBody, 0, "SLOT ONE");   // non-blank name -> not IsEmpty
        LibRefs.SetSetListSlotRef(setListBody, 0, fbCombi0, 0, type: 0);       // slot 0 -> Combi (type 0), index 0 (Combi X)

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void WriteBank(string tag, int count, int itemSize, int bankId, byte[] record)
        {
            WriteAscii(tag); WriteBE32(0); WriteBE32(0); WriteBE32(count); WriteBE32(itemSize); WriteBE32(bankId);
            ms.Write(record);
        }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        WriteBank("MBK1", 1, programSize, 0x01, progABody);   // bank 0x01 (I-B) - see fbProg0

        using var combis = new MemoryStream();
        combis.Write(combiXBody); combis.Write(combiYBody); combis.Write(combiZBody);
        WriteBank("CBK1", 3, combiSize, 0, combis.ToArray());

        WriteBank("SBK1", 1, setListSize, 0, setListBody);

        return ms.ToArray();
    }

    // A second, minimal synthetic PCG carrying just one Program - used to test gap
    // reconciliation (a dependency missing from the first PCG, found in a later one).
    static byte[] BuildSyntheticPcgWithProgram(int fbBank, string name, int number)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize;
        var body = new byte[programSize];
        Encoding.ASCII.GetBytes(name).CopyTo(body, 0);

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        // bankId 0x20000+N maps to U-A..U-GG (N=0 -> 0x40); `number` places the record at the
        // right index within a `number+1`-record bank so its ObjLoc.Number matches exactly.
        WriteAscii("MBK1"); WriteBE32(0); WriteBE32(0); WriteBE32(number + 1); WriteBE32(programSize); WriteBE32(0x20000);
        for (int i = 0; i < number; i++) ms.Write(new byte[programSize]);
        ms.Write(body);

        return ms.ToArray();
    }
}
