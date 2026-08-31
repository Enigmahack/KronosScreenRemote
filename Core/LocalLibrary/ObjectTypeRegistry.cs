namespace KronosScreenRemote;

// Per-object-type behavior, so Core/UI code iterates the registry instead of
// hardcoding switch/if-chains per LibObj type. Each descriptor DELEGATES to the
// existing KronosBanks/LibObj logic - it does not reimplement it.
//
// This is the extension seam for future object types (DrumKits, Wave Sequences,
// read-only GM-bank browsing, all named as likely v2 additions): adding one means
// writing one new IObjectTypeDescriptor + one new Core/ObjectBody decoder, without
// touching LibrarianModel.cs, BatchMoveModel.cs, or any registry-driven UI code.
// Deliberately populated with exactly the 3 real types that exist today - no
// speculative stub entries for types nobody has asked for yet.
interface IObjectTypeDescriptor
{
    int ObjType { get; }
    string DisplayName { get; }
    bool IsReferrer { get; }        // can this type's bodies reference other objects?
    bool IsReferencable { get; }    // can other objects reference this type?

    // The "type" selector of the func-33 reference encoding (KronosBanks.Func33ToObjBank /
    // ObjBankToFunc33), or null for a type that has no representation in it. Distinct from
    // IsReferencable: a Drum Kit IS referencable, but through the LINEAR ms-number encoding,
    // not this one. Null is therefore also the answer to "can a Set List slot address this?" -
    // a slot stores exactly this selector, so a type without one cannot be a set-list target.
    int? Func33RefType { get; }
    string BankLabel(int bank);
    bool IsReadOnlyBank(int bank);

    // WRITABLE banks only - the pull scope (LibraryPullPlanner.AllBanks) and, above all, the
    // WRITE scope: LocalEditOps.FindBankWithFreeSlot and LocalLibraryCache.BackfillInitFlags
    // both iterate this, so anything listed here is somewhere the app may put an object.
    IEnumerable<int> EditableBanks();

    // Factory banks that can be BROWSED but never written - the read-only GM/g Program banks.
    // Deliberately NOT part of EditableBanks: every write path iterates that one, and folding
    // these in would silently make each of them a legal destination. Bodies are never pulled
    // for these; only names are shown (see LocalLibraryPaneViewModel's read-only name source).
    IEnumerable<int> ReadOnlyBanks();

    // Everything the Local Library TREE shows: writable banks plus read-only ones. Display
    // scope only - never a write scope.
    IEnumerable<int> BrowsableBanks() => EditableBanks().Concat(ReadOnlyBanks());

    // Per-BANK, not per-type: Program/Combi/Set List happen to be a uniform 128 everywhere,
    // but Drum Kit (Int=40/User=16) and Wave Sequence (Int=150/User=32) are not - a single
    // type-wide count here would either miss real slots (undercount, silently truncating a
    // sweep) or expose nonexistent slot numbers as legal write destinations (overcount - see
    // DrumKitDescriptor/WaveSequenceDescriptor for why that's the unsafe direction).
    int SlotCount(int bank);
}

static class ObjectTypeRegistry
{
    sealed class ProgramDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.Program;
        public string DisplayName => "Program";
        public bool IsReferrer => true;    // Drum Track -> Program; HD-1 oscillator zones -> Drum Kit/Wave Sequence
        public bool IsReferencable => true;
        public int? Func33RefType => 1;
        public string BankLabel(int bank) => KronosBanks.ProgramLabel(bank);
        public bool IsReadOnlyBank(int bank) => KronosBanks.IsReadOnlyProgramBank(bank);

        // SIX internal banks (I-A..I-F), not seven. Object-dump bank 0x06 ("I-G") is not a real
        // Program bank on a Kronos - the same miscount KronosBanks.Func33ToObjBank and
        // ProgramBankTypeBitIndex each already document from their own side. Listing it here cost
        // twice: every Sync Library swept its 128 slots individually (it answers no bank digest and
        // no bulk dump, so both fast paths fell through) and returned nothing, AND - because
        // IsReadOnlyProgramBank only covers 0x10..0x1A - it stayed a legal auto-fill destination,
        // i.e. a write aimed at a bank that does not exist. Combi I-G is unaffected: Combi really
        // does have seven internal banks.
        public IEnumerable<int> EditableBanks() => Enumerable.Range(0x00, 6).Concat(Enumerable.Range(0x40, 14));

        // GM, g(1)..g(9), g(d) - factory ROM programs. Browsable, never a destination.
        public IEnumerable<int> ReadOnlyBanks() => Enumerable.Range(0x10, 11);
        public int SlotCount(int bank) => 128;
    }

    sealed class CombiDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.Combi;
        public string DisplayName => "Combi";
        public bool IsReferrer => true;
        public bool IsReferencable => true;
        public int? Func33RefType => 0;
        public string BankLabel(int bank) => KronosBanks.CombiLabel(bank);
        public bool IsReadOnlyBank(int bank) => false;
        // Combi genuinely has SEVEN internal banks (I-A..I-G) - see KronosBanks.Func33ToObjBank.
        public IEnumerable<int> EditableBanks() => Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 7));
        public IEnumerable<int> ReadOnlyBanks() => Enumerable.Empty<int>();
        public int SlotCount(int bank) => 128;
    }

    sealed class SetListDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.SetList;
        public string DisplayName => "Set List";
        public bool IsReferrer => true;
        public bool IsReferencable => false;   // nothing ever references a Set List
        public int? Func33RefType => null;
        public string BankLabel(int bank) => "Set Lists";
        public bool IsReadOnlyBank(int bank) => false;
        public IEnumerable<int> EditableBanks() => new[] { 0 };   // flat 128-slot pseudo-bank
        public IEnumerable<int> ReadOnlyBanks() => Enumerable.Empty<int>();
        public int SlotCount(int bank) => SetListData.MaxCount;
    }

    // KRONOS_MIDI_SysEx.txt *2: bank 0 = INT, 0x10 = GM (read-only), 0x40-0x4D = USER-A..GG
    // (14). Slot counts per bank are NOT stated in the MIDI doc - encoded here from
    // kronosology's .pcg-corpus-confirmed DBK1 chunk counts (Int=40, each User=16), on the
    // assumption the file's declared bank sizes match the live instrument's (unverified against
    // real hardware, but itemSize already independently matches Korg's documented Object Version
    // 3 dump size). GM's real slot count is unknown - never queried, since IsReadOnlyBank routes
    // that bank through ReadOnlyNamesIn instead (see LocalLibraryPaneViewModel.BanksFor).
    sealed class DrumKitDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.DrumKit;
        public string DisplayName => "Drum Kit";
        public bool IsReferrer => false;    // references samples (Bank UUID + Id), not other Librarian objects
        public bool IsReferencable => true;    // Drums-mode HD-1 oscillator zones reference these
        public int? Func33RefType => null;
        public string BankLabel(int bank) => KronosBanks.DrumKitLabel(bank);
        public bool IsReadOnlyBank(int bank) => KronosBanks.IsReadOnlyDrumKitBank(bank);
        public IEnumerable<int> EditableBanks() => new[] { 0 }.Concat(Enumerable.Range(0x40, 14));
        public IEnumerable<int> ReadOnlyBanks() => new[] { 0x10 };
        public int SlotCount(int bank) => bank == 0 ? 40 : 16;
    }

    // Same bank layout as Drum Kit, minus GM (Wave Seq has no read-only factory bank per
    // KRONOS_MIDI_SysEx.txt *2). Slot counts likewise from .pcg-corpus WBK1 counts (Int=150,
    // each User=32).
    sealed class WaveSequenceDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.WaveSequence;
        public string DisplayName => "Wave Sequence";
        public bool IsReferrer => false;
        public bool IsReferencable => true;    // HD-1 oscillator zones reference these
        public int? Func33RefType => null;
        public string BankLabel(int bank) => KronosBanks.WaveSeqLabel(bank);
        public bool IsReadOnlyBank(int bank) => false;
        public IEnumerable<int> EditableBanks() => new[] { 0 }.Concat(Enumerable.Range(0x40, 14));
        public IEnumerable<int> ReadOnlyBanks() => Enumerable.Empty<int>();
        public int SlotCount(int bank) => bank == 0 ? 150 : 32;
    }

    static readonly Dictionary<int, IObjectTypeDescriptor> _byType = new()
    {
        [LibObj.Program]      = new ProgramDescriptor(),
        [LibObj.Combi]        = new CombiDescriptor(),
        [LibObj.SetList]      = new SetListDescriptor(),
        [LibObj.DrumKit]      = new DrumKitDescriptor(),
        [LibObj.WaveSequence] = new WaveSequenceDescriptor(),
    };

    public static IObjectTypeDescriptor Get(int objType) => _byType[objType];

    // Both directions of the func-33 reference "type" selector, kept adjacent so the pair stays
    // in lockstep by construction rather than by comment (ReferrersOf encodes, BuildReferrerIndex
    // decodes). Null-tolerant on purpose - unlike Get, these are reached with an ObjLoc whose
    // ObjType may have no descriptor at all (LibObj.Global arrives from a loaded .pcg), and the
    // answer there is "not func-33 addressable", not a throw.
    public static int? Func33RefType(int objType) =>
        _byType.TryGetValue(objType, out var d) ? d.Func33RefType : null;

    public static int? ObjTypeForFunc33RefType(int refType)
    {
        foreach (var d in _byType.Values)
            if (d.Func33RefType == refType) return d.ObjType;
        return null;
    }

    // Does this address sit in a read-only factory bank? Read-only rows are shown in the Local
    // pane as browsable leaves WITH a real Loc (so they label and select like any other row),
    // which means they can also be handed to actions that take an ObjLoc. Most of those already
    // fail harmlessly - every LocalEditOps edit resolves the slot through the cache first, and a
    // GM slot has no cache entry - but "harmless" arrives as a confusing message ("not marked for
    // deletion"), so the entry points that act on a SELECTION filter on this instead.
    public static bool IsReadOnly(ObjLoc loc) => Get(loc.ObjType).IsReadOnlyBank(loc.Bank);
    public static IEnumerable<IObjectTypeDescriptor> All => _byType.Values;
}
