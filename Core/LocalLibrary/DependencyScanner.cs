namespace KronosScreenRemote;

// Walks a Combi/Set List body's own outgoing references (Combi timbre -> Program; Set List
// slot -> Combi/Program), independent of what "resolves" a reference — callers decide how to
// interpret an ObjLoc (does it exist in the Local Library? in a loaded PCG? nowhere?). Reused
// by DependencyScanner.Scan/HasAllDependencies below and by MergeCache's own transitive
// auto-pull, so the reference-site layout (Combi timbre bytes, Set List slot bytes) is
// decoded in exactly one place regardless of which cache is being checked against.
static class ObjectReferenceWalker
{
    public static IEnumerable<(string RefKind, int Site, ObjLoc Ref)> Walk(int objType, byte[] body)
    {
        if (objType == LibObj.Combi)
        {
            foreach (var (t, fbank, num) in LibRefs.IterCombiTimbreRefs(body))
            {
                int objBank = KronosBanks.Func33ToObjBank(1, fbank);   // combi timbres always reference Programs
                if (objBank < 0) continue;
                yield return ($"timbre {t + 1}", t, new ObjLoc(LibObj.Program, objBank, num));
            }
            yield break;
        }

        if (objType != LibObj.SetList) yield break;

        // Decode via SetListBody (not raw LibRefs iteration) specifically so we can skip
        // blank/unused slots via SetListSlot.IsEmpty — every unused slot's default
        // zero-valued reference would otherwise look like a real (and near-always
        // already-satisfied, misleadingly so) dependency on Program/Combi bank 0 slot 0.
        var decoded = SetListBody.FromRawBody(0, body);
        if (decoded == null) yield break;

        foreach (var slot in decoded.Slots)
        {
            // Blank slot, or a Song ref (out of scope, req. 3) — Songs will walk through this
            // exact same yield once supported, just with slot.Type == 2 handled like the other
            // two instead of skipped, and a LibObj.Song constant to yield instead of Program/Combi.
            if (slot.IsEmpty || slot.Type == 2) continue;
            int objBank = KronosBanks.Func33ToObjBank(slot.Type, slot.Bank);   // slot.Type: 0=combi,1=program — same convention as Func33ToObjBank
            if (objBank < 0) continue;
            var refObjType = slot.Type == 1 ? LibObj.Program : LibObj.Combi;
            yield return ($"slot {slot.Number + 1}", slot.Number, new ObjLoc(refObjType, objBank, slot.Index));
        }
    }
}

// Scans an incoming object body (about to be placed into the local pane, e.g. from a
// drag-drop-import or a clipboard paste) for references to other objects, and reports
// which of those references don't resolve locally yet.
static class DependencyScanner
{
    public static IEnumerable<(ObjLoc MissingRef, string RefKind)> Scan(LocalLibraryCache cache, int incomingObjType, byte[] incomingBody) =>
        ObjectReferenceWalker.Walk(incomingObjType, incomingBody)
            .Where(r => cache.GetCurrentBody(r.Ref.ObjType, r.Ref.Bank, r.Ref.Number) == null)
            .Select(r => (r.Ref, r.RefKind));

    // Index-only existence check (no blob reads) — for anything called once PER NODE across a
    // whole tree (e.g. the Local Library tree's dependency-completeness marker), where Scan's
    // GetCurrentBody-per-reference cost (fine for a single placement action) would re-read
    // every referenced blob on every tree refresh — the same synchronous full-disk-read stall
    // already fixed once for LocalLibraryCache.BuildCatalog.
    public static bool HasAllDependencies(LocalLibraryCache cache, int objType, byte[] body) =>
        ObjectReferenceWalker.Walk(objType, body).All(r => cache.Exists(r.Ref.ObjType, r.Ref.Bank, r.Ref.Number));

    // Direct PCG -> Local placement (LibrarianShellViewModel.PlaceFromPcg/BatchPlaceFromPcg)
    // never patches references at all today — every outgoing reference is left exactly as the
    // PCG encoded it (a raw bank/slot address). This walks the ORIGINAL body's own references
    // exactly once — before anything is patched, so "expected content" always comes from the
    // loaded PCG at the reference's ORIGINAL address, never from wherever a repoint below just
    // moved it to (re-deriving "expected" from an already-correct new address would make a
    // successful repoint look like its own mismatch) — and for each one:
    //   - looks up what the PCG itself holds at that address (the one place this object's own
    //     unpatched reference actually points) to compute the dependency's real content hash;
    //   - searches the WHOLE Local Library for that content (LocalLibraryCache.
    //     FindByContentHash) and repoints the byte site there if found, wherever it lives —
    //     not just at the literal address the reference happens to encode;
    //   - otherwise reports it in Unresolved, carrying whatever expected hash was computed (null
    //     if the PCG itself doesn't even have it — a true gap, not repairable by this mechanism)
    //     so the caller can track it (LibrarianShellViewModel.TrackPcgDependencies) and, if the
    //     PCG DOES have it, auto-stage it into the Merge Window instead of leaving a silently
    //     wrong/missing reference.
    public static (byte[] Body, List<(string RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)> Unresolved)
        RepointPcgReferences(LocalLibraryCache cache, PcgLibraryView pcg, int objType, byte[] body)
    {
        var patched = (byte[])body.Clone();
        var unresolved = new List<(string, int, ObjLoc, string?)>();
        foreach (var (refKind, site, refLoc) in ObjectReferenceWalker.Walk(objType, body))
        {
            var pcgEntry = pcg.Get(refLoc);
            var expectedBody = pcgEntry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(refLoc.ObjType, pcgEntry);
            string? expectedHash = expectedBody == null ? null : LocalObjectStore.ComputeHash(expectedBody);

            var foundLoc = expectedHash != null ? cache.FindByContentHash(refLoc.ObjType, expectedHash) : null;
            if (foundLoc is { } d)
            {
                int refType = d.ObjType == LibObj.Program ? 1 : 0;
                int func33Bank = KronosBanks.ObjBankToFunc33(refType, d.Bank);
                if (refKind.StartsWith("timbre", StringComparison.Ordinal))
                    LibRefs.SetCombiTimbreRef(patched, site, func33Bank, d.Number);
                else
                    LibRefs.SetSetListSlotRef(patched, site, func33Bank, d.Number, type: null);
            }
            else
            {
                unresolved.Add((refKind, site, refLoc, expectedHash));
            }
        }
        return (patched, unresolved);
    }
}
