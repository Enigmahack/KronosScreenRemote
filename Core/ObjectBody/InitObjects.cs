namespace KronosScreenRemote;

// "Is this slot actually holding anything?" — the one place that answers it for every object type.
//
// The Kronos protocol has no empty slot and no delete (see EraseBody's own comment): an unused
// Program or Combi holds a full, valid INIT object whose bytes are structurally identical to a real
// patch's. Two separate parts of the Librarian have to know the difference, and both were getting
// it wrong in the same direction — treating a placeholder as if it were data the user cares about:
//
//   • DEPENDENCIES (ObjectReferenceWalker.Walk). An INIT Combi's 16 timbres all point at the
//     zero default — Program I-A:000 — so every init Combi in a library reads as "depends on
//     I-A:000". With a library that doesn't hold I-A:000, that's one phantom unresolved dependency
//     per init Combi ("I-A:000 is needed by 70 objects"), drowning the real ones and blocking the
//     push. An INIT object references nothing meaningful, so it contributes no dependencies at all.
//
//   • PLACEMENT (BatchLibrarian.PlanBatchMove's orphan gate). Overwriting a slot whose occupant is
//     merely INIT destroys nothing, so it must not demand a Force Overwrite the way a real,
//     still-referenced patch does.
//
// Detection is by SHAPE and NAME, deliberately not by a table of known-init content hashes. A hash
// table would have to be captured from the instrument first (BlankTemplateStore only fills in when
// a pending-delete is committed), would differ per Kronos OS revision and per HD-1/EXi format, and
// would silently stop matching after any firmware update — while the properties below hold for
// every init object of every revision, with no capture step and no I/O. Where a captured template
// DOES exist, its content hash necessarily satisfies these same checks.
static class InitObjects
{
    public static bool IsInit(int objType, byte[] body) => objType switch
    {
        LibObj.Program => ProgramBody.IsInit(body),
        LibObj.Combi   => CombiBody.IsInit(body),
        // A Set List has no "Init Set List" name convention to key off — a factory-untouched one
        // comes back named "Set List 000".."Set List 127" (SetListData.SlotDefaultName), which a
        // user could equally have typed themselves. Its emptiness is the AGGREGATE of its slots,
        // which is why this needs the body and can't be answered from a cached display name the
        // way Programs and Combis can. Either slot-level signal is enough — see AllSlotsAtDefault
        // for why the name-blank one (SetListData.IsEmpty) can't carry this alone.
        LibObj.SetList => SetListBody.FromRawBody(0, body) is { } setList
                          && (setList.IsEmpty || AllSlotsAtDefault(setList)),
        _              => false,
    };

    // Every slot still points at the zero default — Program I-A:000 — which is the encoding of
    // "nothing assigned", exactly as an init Combi's 16 timbres all point at bank 0/program 0
    // (CombiBody.AllTimbresAtDefault). This is the DEFINING property of an untouched Set List and
    // holds regardless of what the object or its slots are NAMED: the instrument ships them named
    // "Set List 000".."Set List 127", and a user can rename a set list without ever assigning
    // anything to it, so SetListData.IsEmpty (which keys on blank slot names) misses both cases.
    //
    // False for a body that decoded to fewer than the full 128 slots (a truncated dump) — the same
    // guard AllTimbresAtDefault uses, so "fewer than 128 defaults" can't read as "all defaults".
    static bool AllSlotsAtDefault(SetListData setList)
    {
        if (setList.Slots.Count != SetListData.SlotCount) return false;
        // NOTE the literal 1: ObjBankToFunc33's first argument is the set-list/timbre SLOT TYPE
        // code (1 = program, 0 = combi), which is not LibObj.Program — that constant is 0x00 and
        // would silently select the combi bank mapping here.
        int defaultProgBank = KronosBanks.ObjBankToFunc33(1, 0x00);   // Program I-A as a func-33 bank
        foreach (var slot in setList.Slots)
            if (slot.Type != 1 || slot.Bank != defaultProgBank || slot.Index != 0) return false;   // type 1 = Prog
        return true;
    }

    // Name-only sibling, for callers holding a cached display name that must not pay a blob read
    // (LocalLibraryCache.IsInitSlot scans whole banks — see ProgramBody.IsInitName's own comment).
    //
    // For a PROGRAM this is not a weaker answer at all: ProgramBody.IsInit IS the name check, so
    // this is exact. For a COMBI it catches the name signal but not AllTimbresAtDefault, so it can
    // under-report (an unnamed init Combi reads as real content) — safe in the direction that
    // matters, since the cost is a placeholder that doesn't get auto-filled over, never a real
    // patch that does. Set Lists always return false here; only the body can answer for them.
    public static bool IsInitName(int objType, string name) => objType switch
    {
        LibObj.Program => ProgramBody.IsInitName(name),
        LibObj.Combi   => CombiBody.IsInitName(name),
        _              => false,
    };
}
