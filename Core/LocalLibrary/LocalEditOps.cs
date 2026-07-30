namespace KronosScreenRemote;

// Every user-facing local edit action, running against LocalLibraryCache instead of
// hardware. Move/BatchPlace/PlaceObject call Librarian.PlanMove/BatchLibrarian.PlanBatchMove
// UNCHANGED — referrer-patch computation happens exactly once, here, at edit time, and is
// never redone at push time (architectural spine points 1/2 of the rebuild plan).
static class LocalEditOps
{
    public static ObjectDump? GetObjectDump(LocalLibraryCache cache, ObjLoc loc)
    {
        var body = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
        var version = cache.GetVersion(loc.ObjType, loc.Bank, loc.Number);
        return body != null && version != null ? new ObjectDump(loc.ObjType, loc.Bank, loc.Number, version.Value, body) : null;
    }

    public static (bool Ok, string? Error) Rename(LocalLibraryCache cache, ObjLoc loc, string newName, DateTime utcNow)
    {
        var dump = GetObjectDump(cache, loc);
        if (dump == null) return (false, "not found locally — Pull first");

        byte[] renamed = loc.ObjType switch
        {
            LibObj.Program => ProgramBody.WriteName(dump.Body, newName),
            LibObj.Combi   => CombiBody.WriteName(dump.Body, newName),
            LibObj.SetList => SetListBody.WriteName(dump.Body, newName),
            _ => dump.Body,
        };
        cache.RecordEdit(loc.ObjType, loc.Bank, loc.Number, dump.Version, renamed,
            "Rename", $"Renamed {loc.Label()} to \"{newName}\"", utcNow);
        return (true, null);
    }

    // Swap src<->dst. `active` is always null here (never the caller's choice) — the
    // concrete, one-line realization of requirement 17 (live 0x43 preview dropped for v1):
    // Librarian.PlanMove's `active` parameter already defaults to optional, so omitting it
    // is a call-site choice, not a LibrarianModel.cs change.
    public static (bool Ok, string? Error) Move(LocalLibraryCache cache, ObjLoc src, ObjLoc dst, DateTime utcNow)
    {
        var srcDump = GetObjectDump(cache, src);
        var dstDump = GetObjectDump(cache, dst);
        if (srcDump == null || dstDump == null) return (false, "source or destination not found locally — Pull first");

        var cat = cache.BuildCatalog();
        var plan = Librarian.PlanMove(cat, src, srcDump, dst, dstDump);
        if (plan.IsRefusable) return (false, string.Join("; ", plan.Warnings));

        cache.RecordEdits(plan.Writes.Select(w => (w.Obj, w.Bank, w.Index, w.Version, w.Body)),
            "Move", $"Moved {src.Label()} ↔ {dst.Label()}", utcNow);
        return (true, null);
    }

    // N-item relocation into one destination bank (never mixing Program/Combi) — the
    // clipboard/drag-drop-import batch flow. Returns any NEW clipboard entries the plan
    // produced (displaced occupants diverted to the PERSISTED clipboard) for the caller to
    // merge into its BatchClipboard and save.
    public static (bool Ok, string? Error, List<ClipboardEntry> NewClipboardEntries) BatchPlace(
        LocalLibraryCache cache, int objType, IReadOnlyList<BatchPlacement> placements,
        bool divertDisplacedToClipboard, Func<int, bool?>? bankTypeOf, DateTime utcNow, bool forceOverwrite = false)
    {
        var cat = cache.BuildCatalog();
        var destOccupants = new Dictionary<ObjLoc, ObjectDump>();
        foreach (var p in placements)
        {
            var occ = GetObjectDump(cache, p.To);
            if (occ != null) destOccupants[p.To] = occ;
        }

        var plan = BatchLibrarian.PlanBatchMove(cat, objType, placements, destOccupants, divertDisplacedToClipboard, bankTypeOf, forceOverwrite);
        if (plan.IsRefusable) return (false, string.Join("; ", plan.Warnings), new List<ClipboardEntry>());

        cache.RecordEdits(plan.Writes.Select(w => (w.Obj, w.Bank, w.Index, w.Version, w.Body)),
            "BatchPlace", $"Placed {placements.Count} item(s) into {ObjectTypeRegistry.Get(objType).DisplayName} bank(s)", utcNow);
        return (true, null, plan.ClipboardAdds);
    }

    // The one general placement primitive: a single item with no local origin to repoint
    // referrers from — a PCG-pane drag-drop or a session-clipboard Copy. There is no local
    // "move" variant of this: BatchMoveModel.cs's `From` repoint path never vacates the source
    // slot (writes only ever land at `To`), and this cache has no primitive that represents
    // "this slot should become empty" for hardware to push later — Discard only reverts a
    // pending edit back to baseline, it doesn't clear a clean slot. A same-library Move is
    // therefore only ever done via the true, symmetric swap below (both directions written),
    // never via PlaceObject + a hoped-for vacate step.
    public static (bool Ok, string? Error, List<ClipboardEntry> NewClipboardEntries) PlaceObject(
        LocalLibraryCache cache, ObjLoc dest, int objType, byte version, byte[] sourceBody, string sourceLabel,
        bool divertDisplacedToClipboard, DateTime utcNow, Func<int, bool?>? bankTypeOf = null, bool forceOverwrite = false)
    {
        var placement = new BatchPlacement(null, dest, new ObjectDump(objType, dest.Bank, dest.Number, version, sourceBody), sourceLabel);
        return BatchPlace(cache, objType, new[] { placement }, divertDisplacedToClipboard, bankTypeOf, utcNow, forceOverwrite);
    }

    // Repoints ONE reference site inside an already-placed Combi/Set List to a NEW destination
    // — the repair half of the auto-heal placement pipeline (LibrarianShellViewModel.
    // ResolvePendingDependencies): a dependency that wasn't resolvable when `requiredBy` was
    // originally placed has since turned up somewhere in Local Library (found by content hash,
    // not necessarily at the address the reference originally encoded), so its reference needs
    // rewriting to point there. This is a REAL edit — goes through RecordEdit like any other
    // local change (re-dirties requiredBy, appends OpLog/History, feeds the next push
    // changeset) — never a silent byte mutation bypassing that bookkeeping, since requiredBy
    // may already be dirty or even previously pushed. Returns false if requiredBy itself is no
    // longer present locally (e.g. discarded/deleted since it was tracked).
    public static bool RepatchReference(LocalLibraryCache cache, ObjLoc requiredBy, int site, string refKind, ObjLoc newTarget, DateTime utcNow)
    {
        var dump = GetObjectDump(cache, requiredBy);
        if (dump == null) return false;

        var body = (byte[])dump.Body.Clone();
        int refType = newTarget.ObjType == LibObj.Program ? 1 : 0;
        int func33Bank = KronosBanks.ObjBankToFunc33(refType, newTarget.Bank);
        if (refKind.StartsWith("timbre", StringComparison.Ordinal))
            LibRefs.SetCombiTimbreRef(body, site, func33Bank, newTarget.Number);
        else
            LibRefs.SetSetListSlotRef(body, site, func33Bank, newTarget.Number, type: null);

        cache.RecordEdit(requiredBy.ObjType, requiredBy.Bank, requiredBy.Number, dump.Version, body,
            "RepatchReference", $"Repointed a reference in {requiredBy.Label()} to {newTarget.Label()}", utcNow);
        return true;
    }

    // Whole-object properties: Name (all three types), Category/Sub-Category (Program/Combi
    // only — Set Lists have no category field). Pass null for anything left unchanged.
    public static (bool Ok, string? Error) EditProperties(
        LocalLibraryCache cache, ObjLoc loc, string? name, int? category, int? subCategory, DateTime utcNow)
    {
        var dump = GetObjectDump(cache, loc);
        if (dump == null) return (false, "not found locally — Pull first");
        var body = dump.Body;
        var changes = new List<string>();

        if (name != null)
        {
            body = loc.ObjType switch
            {
                LibObj.Program => ProgramBody.WriteName(body, name),
                LibObj.Combi   => CombiBody.WriteName(body, name),
                LibObj.SetList => SetListBody.WriteName(body, name),
                _ => body,
            };
            changes.Add($"name to \"{name}\"");
        }
        if (category is int cat && subCategory is int sub)
        {
            if (loc.ObjType == LibObj.Program) { body = ProgramBody.WriteCategory(body, cat, sub); changes.Add($"category to {cat}/{sub}"); }
            else if (loc.ObjType == LibObj.Combi) { body = CombiBody.WriteCategory(body, cat, sub); changes.Add($"category to {cat}/{sub}"); }
        }

        if (changes.Count == 0) return (false, "nothing to change");
        cache.RecordEdit(loc.ObjType, loc.Bank, loc.Number, dump.Version, body,
            "PropertyEdit", $"Edited {loc.Label()}: {string.Join(", ", changes)}", utcNow);
        return (true, null);
    }

    // Set List SLOT properties are a distinct addressing dimension from EditProperties
    // above — `slot` (0-127) selects a record WITHIN the Set List body at `loc`, not the
    // Set List object's own name field.
    public static (bool Ok, string? Error) EditSetListSlot(
        LocalLibraryCache cache, ObjLoc loc, int slot, string? name, int? color, string? comments, DateTime utcNow)
    {
        if (loc.ObjType != LibObj.SetList) return (false, "not a Set List");
        var dump = GetObjectDump(cache, loc);
        if (dump == null) return (false, "not found locally — Pull first");
        var body = dump.Body;
        var changes = new List<string>();

        if (name != null) { body = SetListBody.WriteSlotName(body, slot, name); changes.Add($"slot {slot} name to \"{name}\""); }
        if (color != null) { body = SetListBody.WriteSlotColor(body, slot, color.Value); changes.Add($"slot {slot} color to {color}"); }
        if (comments != null) { body = SetListBody.WriteSlotComments(body, slot, comments); changes.Add($"slot {slot} comments"); }

        if (changes.Count == 0) return (false, "nothing to change");
        cache.RecordEdit(loc.ObjType, loc.Bank, loc.Number, dump.Version, body,
            "PropertyEdit", $"Edited {loc.Label()}: {string.Join(", ", changes)}", utcNow);
        return (true, null);
    }

    public static (bool Ok, string? Error) Discard(LocalLibraryCache cache, ObjLoc loc, DateTime utcNow) =>
        cache.Discard(loc.ObjType, loc.Bank, loc.Number, utcNow)
            ? (true, null)
            : (false, "nothing pending to discard");

    public static (bool Ok, string? Error) SetPendingDelete(LocalLibraryCache cache, ObjLoc loc, bool value, DateTime utcNow) =>
        cache.SetPendingDelete(loc.ObjType, loc.Bank, loc.Number, value, utcNow)
            ? (true, null)
            : (false, value ? "already marked for deletion" : "not marked for deletion");

    // Shared by every auto-fill entry point (PCG batch placement, and now the Local pane's
    // own Paste-onto-a-bank) — first empty slot in bank order, or 0 if the bank is full
    // (callers that care about "full" find out via the placement/fill result, same as
    // LibrarianShellViewModel's PCG-batch path already relies on).
    public static int FindNextFreeSlot(LocalLibraryCache cache, int objType, int bank)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        for (int i = 0; i < descriptor.SlotCount; i++)
            if (!cache.Exists(objType, bank, i)) return i;
        return 0;
    }

}
