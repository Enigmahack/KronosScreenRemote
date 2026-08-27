namespace KronosScreenRemote;

// FALLBACK blank-body builder for a committed pending-delete, used only when no
// REAL captured blank template is available (offline AND never captured - see
// BlankTemplates/ChangesetBuilder, which prefer the instrument's own blank bytes). This derives a
// best-effort blank from the object's existing body.
//
// The Kronos SysEx protocol has NO "delete object" command - func 0x73 always
// writes a body, and every Program/Combi/Set-List slot on the instrument always contains
// something - so the only way to make a slot "empty" on hardware is to overwrite it with a
// blank/initialized object and Store it.
//
// Every erase body is DERIVED from the slot's existing (valid) body rather than synthesized
// from scratch: same wire length and structure, only the identity fields cleared. That keeps
// the result a body the instrument is guaranteed to accept - the whole write still goes through
// ApplyMoveAsync's pre-image backup + staleness gate + Reply-code abort, so a rejected write
// fails safe. Set-List erase is a true empty (all slots blanked); Program/Combi erase is a
// reset-to-INIT identity (name/category cleared, and a Combi's timbre references cleared so it
// points at nothing) rather than a factory-accurate INIT patch set, which isn't available
// offline - see the plan's Verification note.
static class EraseBody
{
    public static byte[] Build(int objType, byte[] existingBody) => objType switch
    {
        LibObj.SetList      => BuildSetList(existingBody),
        LibObj.Combi        => BuildCombi(existingBody),
        LibObj.Program      => BuildProgram(existingBody),
        LibObj.DrumKit      => DrumKitBody.WriteName(existingBody, "Init Drum Kit"),
        LibObj.WaveSequence => WaveSequenceBody.WriteName(existingBody, "Init Wave Sequence"),
        _                   => (byte[])existingBody.Clone(),
    };

    // Empty Set List: blank the object's own name and every one of its 128 slot names + comments.
    // A slot's blank name is exactly what SetListData.SlotIsEmpty (and therefore
    // SetListData.IsEmpty) keys off, so this decodes to IsEmpty == true and reads as an empty
    // set list throughout this app. Each SetListBody.WriteX returns a fresh clone, so `body` is
    // reassigned each step.
    //
    // Deliberately does NOT rewrite each slot's performance type/bank/index reference: the Set
    // List slot format (Documentation/MIDI implementation/SysExDumps/SetList.txt) has NO
    // "unassigned" encoding - every slot always references *some* prog/combi/song - so zeroing a
    // ref just makes it point at Combi INT-A:001 rather than "nothing," which is more misleading,
    // not less. Leaving the original (known-valid) references in place while blanking the names
    // is the least-invasive "empty" this protocol allows. HOW A REAL KRONOS RENDERS a blank-named
    // slot is unverified here (no empty-set-list dump exists in-repo to pin the exact bytes) - see
    // the plan's Verification note; test on a spare Set List before relying on it.
    static byte[] BuildSetList(byte[] existing)
    {
        var body = SetListBody.WriteName(existing, "");
        for (int slot = 0; slot < SetListData.SlotCount; slot++)
        {
            body = SetListBody.WriteSlotName(body, slot, "");
            body = SetListBody.WriteSlotComments(body, slot, "");
        }
        return body;
    }

    // Reset-to-INIT Combi: blank name + category, and clear all 16 timbre references so the
    // erased Combi points at nothing (no dangling references left behind). Wire length preserved.
    static byte[] BuildCombi(byte[] existing)
    {
        var body = CombiBody.WriteName(existing, "INIT COMBI");
        body = CombiBody.WriteCategory(body, 0, 0);
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(body, t, func33Bank: 0, number: 0);
        return body;
    }

    // Reset-to-INIT Program: blank name + category. Wire length/format (EXi vs HD-1) is
    // inherently preserved - the body is the existing one with only those fields changed.
    static byte[] BuildProgram(byte[] existing)
    {
        var body = ProgramBody.WriteName(existing, "INIT PROGRAM");
        return ProgramBody.WriteCategory(body, 0, 0);
    }
}
