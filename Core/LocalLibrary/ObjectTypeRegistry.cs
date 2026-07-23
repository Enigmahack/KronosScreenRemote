namespace KronosScreenRemote;

// Per-object-type behavior, so Core/UI code iterates the registry instead of
// hardcoding switch/if-chains per LibObj type. Each descriptor DELEGATES to the
// existing KronosBanks/LibObj logic — it does not reimplement it.
//
// This is the extension seam for future object types (DrumKits, Wave Sequences,
// read-only GM-bank browsing, all named as likely v2 additions): adding one means
// writing one new IObjectTypeDescriptor + one new Core/ObjectBody decoder, without
// touching LibrarianModel.cs, BatchMoveModel.cs, or any registry-driven UI code.
// Deliberately populated with exactly the 3 real types that exist today — no
// speculative stub entries for types nobody has asked for yet.
interface IObjectTypeDescriptor
{
    int ObjType { get; }
    string DisplayName { get; }
    bool IsReferrer { get; }        // can this type's bodies reference other objects?
    bool IsReferencable { get; }    // can other objects reference this type?
    string BankLabel(int bank);
    bool IsReadOnlyBank(int bank);
    IEnumerable<int> EditableBanks();
    int SlotCount { get; }
}

static class ObjectTypeRegistry
{
    sealed class ProgramDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.Program;
        public string DisplayName => "Program";
        public bool IsReferrer => false;
        public bool IsReferencable => true;
        public string BankLabel(int bank) => KronosBanks.ProgramLabel(bank);
        public bool IsReadOnlyBank(int bank) => KronosBanks.IsReadOnlyProgramBank(bank);
        public IEnumerable<int> EditableBanks() => Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 14));
        public int SlotCount => 128;
    }

    sealed class CombiDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.Combi;
        public string DisplayName => "Combi";
        public bool IsReferrer => true;
        public bool IsReferencable => true;
        public string BankLabel(int bank) => KronosBanks.CombiLabel(bank);
        public bool IsReadOnlyBank(int bank) => false;
        public IEnumerable<int> EditableBanks() => Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 7));
        public int SlotCount => 128;
    }

    sealed class SetListDescriptor : IObjectTypeDescriptor
    {
        public int ObjType => LibObj.SetList;
        public string DisplayName => "Set List";
        public bool IsReferrer => true;
        public bool IsReferencable => false;   // nothing ever references a Set List
        public string BankLabel(int bank) => "Set Lists";
        public bool IsReadOnlyBank(int bank) => false;
        public IEnumerable<int> EditableBanks() => new[] { 0 };   // flat 128-slot pseudo-bank
        public int SlotCount => SetListData.MaxCount;
    }

    static readonly Dictionary<int, IObjectTypeDescriptor> _byType = new()
    {
        [LibObj.Program] = new ProgramDescriptor(),
        [LibObj.Combi]   = new CombiDescriptor(),
        [LibObj.SetList] = new SetListDescriptor(),
    };

    public static IObjectTypeDescriptor Get(int objType) => _byType[objType];
    public static IEnumerable<IObjectTypeDescriptor> All => _byType.Values;
}
