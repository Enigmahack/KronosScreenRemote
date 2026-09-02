namespace KronosScreenRemote;

// Walks a Combi/Set List body's own outgoing references (Combi timbre -> Program; Set List
// slot -> Combi/Program), independent of what "resolves" a reference - callers decide how to
// interpret an ObjLoc (does it exist in the Keyboard Library? in a loaded PCG? nowhere?). Reused
// by DependencyScanner.Scan/HasAllDependencies below and by MergeCache's own transitive
// auto-pull, so the reference-site layout (Combi timbre bytes, Set List slot bytes) is
// decoded in exactly one place regardless of which cache is being checked against.
static class ObjectReferenceWalker
{
    public static IEnumerable<(RefKind RefKind, int Site, ObjLoc Ref)> Walk(int objType, byte[] body)
    {
        // An INIT/placeholder object references nothing meaningful - its timbres still hold the
        // zero default, which encodes "nothing assigned", not a dependency on Program I-A:000.
        // Walking them anyway made every init Combi in a library report one phantom unresolved
        // dependency on the same address, which then dominated the unresolved list and blocked the
        // push. Same rationale as the blank Set List slot skip below, one level up. See InitObjects.
        if (InitObjects.IsInit(objType, body)) yield break;

        if (objType == LibObj.Combi)
        {
            foreach (var (t, fbank, num) in LibRefs.IterCombiTimbreRefs(body))
            {
                int objBank = KronosBanks.Func33ToObjBank(1, fbank);   // combi timbres always reference Programs
                if (objBank < 0) continue;
                yield return (RefKind.CombiTimbre, t, new ObjLoc(LibObj.Program, objBank, num));
            }
            yield break;
        }

        if (objType == LibObj.Program)
        {
            if (body.Length > LibRefs.ProgramDrumTrackBank && LibRefs.ProgramDrumTrackOn(body))
            {
                var (dtBank, dtNumber) = LibRefs.ProgramDrumTrackRef(body);
                int dtObjBank = KronosBanks.Func33ToObjBank(1, dtBank);
                if (dtObjBank >= 0)
                    yield return (RefKind.DrumTrack, -1, new ObjLoc(LibObj.Program, dtObjBank, dtNumber));
            }

            // Wave Sequence / Drum Kit oscillator zones only exist in the HD-1 wire format -
            // EXi Programs use this same byte range for their own per-slot instrument data.
            if (body.Length == ProgramFormatConverter.WireSizeHd1)
            {
                int oscMode = LibRefs.ProgramOscillatorMode(body);
                foreach (var (osc, zone, msType, number) in LibRefs.IterProgramZoneRefs(body))
                {
                    int site = osc * LibRefs.ZonesPerOsc + zone;
                    if (msType == 2 && KronosBanks.WaveSeqLinearToLoc(number) is { } ws)
                        yield return (RefKind.OscZone, site, new ObjLoc(LibObj.WaveSequence, ws.Bank, ws.Slot));
                    else if (msType == 1 && oscMode is 4 or 5 && KronosBanks.DrumKitLinearToLoc(number) is { } dk)
                        yield return (RefKind.OscZone, site, new ObjLoc(LibObj.DrumKit, dk.Bank, dk.Slot));
                }
            }
            yield break;
        }

        if (objType != LibObj.SetList) yield break;

        // Decode via SetListBody (not raw LibRefs iteration) specifically so we can skip
        // blank/unused slots via SetListSlot.IsEmpty - every unused slot's default
        // zero-valued reference would otherwise look like a real (and near-always
        // already-satisfied, misleadingly so) dependency on Program/Combi bank 0 slot 0.
        var decoded = SetListBody.FromRawBody(0, body);
        if (decoded == null) yield break;

        foreach (var slot in decoded.Slots)
        {
            // Blank slot, or a Song ref (out of scope) - Songs will walk through this
            // exact same yield once supported, just with slot.Type == 2 handled like the other
            // two instead of skipped, and a LibObj.Song constant to yield instead of Program/Combi.
            if (slot.IsEmpty || slot.Type == 2) continue;
            int objBank = KronosBanks.Func33ToObjBank(slot.Type, slot.Bank);   // slot.Type: 0=combi,1=program - same convention as Func33ToObjBank
            if (objBank < 0) continue;
            var refObjType = slot.Type == 1 ? LibObj.Program : LibObj.Combi;
            yield return (RefKind.SetListSlot, slot.Number, new ObjLoc(refObjType, objBank, slot.Index));
        }
    }

    // A reference that can NEVER be missing, because its target isn't part of the library at all:
    // the read-only ROM Program banks (GM, g(1)-g(9), g(d) - object banks 0x10..0x1A). Those banks
    // are factory content burned into the instrument; LibraryPullPlanner deliberately never fetches
    // them (ObjectTypeRegistry.EditableBanks scopes to the 21 writable Program banks), no .pcg file
    // carries them, and nothing can ever place one - so a Combi timbre or Set List slot pointing at
    // GM:012 always resolves ON THE INSTRUMENT, however empty the keyboard library is.
    //
    // Without this, every such reference read as a permanently-unresolvable dependency: a red dot on
    // the Combi forever, a pending session-clipboard entry that no retry could ever clear, and -
    // worst - a hard REFUSE of the whole push from ChangesetBuilder's step-3 referential check. GM
    // references are extremely common - factory Combis use them, and any timbre left pointing at a
    // GM program does too.
    //
    // Deliberately a CLASSIFIER, not a filter inside Walk: the dependency panels still want to SHOW
    // these references (labelled as ROM/always-available), they just must never be treated as
    // something to resolve, pull, repoint, or block on.
    //
    // GM Drum Kit (object bank 0x10) is the same kind of factory ROM content as GM Programs -
    // a Drums-mode oscillator zone pointing at linear 152..160 resolves on the instrument
    // regardless of the keyboard library.
    public static bool IsAlwaysAvailable(ObjLoc reference) =>
        (reference.ObjType == LibObj.Program && KronosBanks.IsReadOnlyProgramBank(reference.Bank)) ||
        (reference.ObjType == LibObj.DrumKit && KronosBanks.IsReadOnlyDrumKitBank(reference.Bank));

    // Walk, minus the references nothing can ever resolve because they don't need resolving - the
    // shape every RESOLUTION path wants (DependencyScanner.Scan/HasAllDependencies/
    // RepointPcgReferences, MergeCache.PullRecursive). Display paths keep using Walk directly.
    public static IEnumerable<(RefKind RefKind, int Site, ObjLoc Ref)> WalkResolvable(int objType, byte[] body) =>
        Walk(objType, body).Where(r => !IsAlwaysAvailable(r.Ref));
}

// Scans an incoming object body (about to be placed into the local pane, e.g. from a
// drag-drop-import or a clipboard paste) for references to other objects, and reports
// which of those references don't resolve locally yet.
static class DependencyScanner
{
    public static IEnumerable<(ObjLoc MissingRef, RefKind RefKind)> Scan(LocalLibraryCache cache, int incomingObjType, byte[] incomingBody) =>
        ObjectReferenceWalker.WalkResolvable(incomingObjType, incomingBody)
            .Where(r => cache.GetCurrentBody(r.Ref.ObjType, r.Ref.Bank, r.Ref.Number) == null)
            .Select(r => (r.Ref, r.RefKind));

    // Push-gate variant (ChangesetBuilder step 3). `beingErased` is what step 4b will blank in
    // this same push: those slots still resolve right up until it runs, so a plain Scan can't
    // see them going away. Deliberately a separate entry point rather than folding the rule
    // into Scan/HasAllDependencies - Scan also serves the paste/drag-drop-import warnings
    // (where a pending delete hasn't happened yet and shouldn't read as a missing dependency),
    // and HasAllDependencies feeds the PERSISTED HasResolvedDependencies flag, which nothing
    // recomputes when SetPendingDelete toggles, so teaching it about pending deletes would
    // only make that cached flag go stale. Keeps Scan's GetCurrentBody test rather than the
    // cheaper index-only Exists: this runs once per push, and Exists would stop catching an
    // indexed slot whose blob has gone missing.
    public static IEnumerable<(ObjLoc MissingRef, RefKind RefKind)> ScanForPush(
        LocalLibraryCache cache, int incomingObjType, byte[] incomingBody, IReadOnlySet<ObjLoc> beingErased) =>
        ObjectReferenceWalker.WalkResolvable(incomingObjType, incomingBody)
            .Where(r => beingErased.Contains(r.Ref)
                        || cache.GetCurrentBody(r.Ref.ObjType, r.Ref.Bank, r.Ref.Number) == null)
            .Select(r => (r.Ref, r.RefKind));

    // Index-only existence check (no blob reads) - for anything called once PER NODE across a
    // whole tree (e.g. the Keyboard Library tree's dependency-completeness marker), where Scan's
    // GetCurrentBody-per-reference cost (fine for a single placement action) would re-read
    // every referenced blob on every tree refresh - the same synchronous full-disk-read stall
    // already fixed once for LocalLibraryCache.BuildCatalog.
    public static bool HasAllDependencies(LocalLibraryCache cache, int objType, byte[] body) =>
        ObjectReferenceWalker.WalkResolvable(objType, body).All(r => cache.Exists(r.Ref.ObjType, r.Ref.Bank, r.Ref.Number));

    // Direct PCG -> Local placement (LibrarianShellViewModel.PlaceFromPcg/BatchPlaceFromPcg)
    // never patches references at all today - every outgoing reference is left exactly as the
    // PCG encoded it (a raw bank/slot address). This walks the ORIGINAL body's own references
    // exactly once - before anything is patched, so "expected content" always comes from the
    // loaded PCG at the reference's ORIGINAL address, never from wherever a repoint below just
    // moved it to (re-deriving "expected" from an already-correct new address would make a
    // successful repoint look like its own mismatch) - and for each one:
    //   - looks up what the PCG itself holds at that address (the one place this object's own
    //     unpatched reference actually points) to compute the dependency's real content hash;
    //   - searches the WHOLE Keyboard Library for that content (LocalLibraryCache.
    //     FindByContentHash) and repoints the byte site there if found, wherever it lives -
    //     not just at the literal address the reference happens to encode;
    //   - otherwise reports it in Unresolved, carrying whatever expected hash was computed (null
    //     if the PCG itself doesn't even have it - a true gap, not repairable by this mechanism)
    //     so the caller can track it (LibrarianShellViewModel.TrackPcgDependencies) and, if the
    //     PCG DOES have it, auto-stage it into the Merge Window instead of leaving a silently
    //     wrong/missing reference.
    public static (byte[] Body, List<(RefKind RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)> Unresolved)
        RepointPcgReferences(LocalLibraryCache cache, PcgLibraryView pcg, int objType, byte[] body)
    {
        var patched = (byte[])body.Clone();
        var unresolved = new List<(RefKind, int, ObjLoc, string?)>();
        // WalkResolvable, not Walk: a GM/g reference is already correct exactly as the PCG encoded
        // it (the ROM bank exists on every Kronos) - repointing it is impossible and reporting it
        // unresolved would stage a dependency that can never be satisfied. See
        // ObjectReferenceWalker.IsAlwaysAvailable.
        foreach (var (refKind, site, refLoc) in ObjectReferenceWalker.WalkResolvable(objType, body))
        {
            var pcgEntry = pcg.Get(refLoc);
            var expectedBody = pcgEntry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(refLoc.ObjType, pcgEntry);
            string? expectedHash = expectedBody == null ? null : LocalObjectStore.ComputeHash(expectedBody);

            var foundLoc = expectedHash != null ? cache.FindByContentHash(refLoc.ObjType, expectedHash) : null;
            if (foundLoc is { } d)
            {
                LibRefs.ApplyResolvedRef(patched, refKind, site, refLoc.ObjType, d.Bank, d.Number);
            }
            else
            {
                unresolved.Add((refKind, site, refLoc, expectedHash));
            }
        }
        return (patched, unresolved);
    }
}
