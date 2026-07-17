namespace KronosScreenRemote;

// Decodes a MIDI Program Change + Bank Select into a Kronos performance identity,
// with zero SysEx — so following program changes off the live stream causes no
// "Transmitting MIDI Data…" flash (unlike a func 0x33 query).
//
// Source: KRONOS MIDI Implementation (8 Aug 2024), "*2 Bank Map".
//   Bank Select MSB = CC0 (mm), LSB = CC32 (bb), Program Change = pp.
//   KORG bank map:  mm = 00 for INT/USER.   GM(2) bank map: mm = 3F (same layout).
//   GM/g banks:     mm = 79 (GM, g(1)-g(9)), mm = 78 (g(d)).
//   Program : bb 00-05 → INT-A..F,  bb 08-15 → USER-A..GG.
//   Combi   : bb 00-06 → INT-A..G,  bb 08-0E → USER-A..G.
//
// ObjBank is the object-dump bank number (KRONOS_MIDI_SysEx.txt *2), used to
// bulk-dump that bank's names via func 0x77; it doubles as the canonical cache key.
readonly record struct BankId(int Type, string Label, int ObjBank, int Number)
{
    // Type: 0 = combi, 1 = program (func 0x33 / RequestCurrentNameAsync convention).
    // GM / g(x) banks are 1-based on the Kronos display though their PC is 0-based;
    // INT/USER are 0-based. Number stays the raw PC (the cache key).
    public string Display
    {
        get
        {
            bool oneBased = Label == "GM" || Label.StartsWith("g(", StringComparison.Ordinal);
            return $"{Label}:{(oneBased ? Number + 1 : Number):D3}";
        }
    }
}

// One persisted program/combi name, keyed by (type, object bank, number).
record CachedName(int Type, int Bank, int Number, string Name);

static class KronosBanks
{
    // stateMode: 3 = Program, 2 = Combi. Other modes return null (use func 0x33).
    public static BankId? Decode(int stateMode, int msb, int lsb, int pc)
    {
        if (stateMode == 3)
        {
            int ob = ProgramObjBank(msb, lsb);
            return ob < 0 ? null : new BankId(1, ProgramLabel(ob), ob, pc);
        }
        if (stateMode == 2)
        {
            int ob = CombiObjBank(msb, lsb);
            return ob < 0 ? null : new BankId(0, CombiLabel(ob), ob, pc);
        }
        return null;
    }

    // The Object Dump / name object type for a performance type.
    //   program → 0x13 (Program Name), combi → 0x12 (Combi Name).
    public static int NameObject(int type) => type == 0 ? 0x12 : 0x13;

    // Build a BankId from a func 0x33 identity (type, func-33 bank index, number)
    // so the func-33 path shares the stream path's cache key + display formatting.
    // Returns null for song/unknown (caller keeps the old formatting there).
    public static BankId? FromFunc33(int type, int func33Bank, int number)
    {
        int ob = Func33ToObjBank(type, func33Bank);
        if (ob < 0) return null;
        string label = type == 1 ? ProgramLabel(ob) : CombiLabel(ob);
        return new BankId(type, label, ob, number);
    }

    // func-33 bank index → object-dump bank number (matches KronosSysEx bank tables).
    // Public: the Librarian's reference fixup translates a combi-timbre / set-list-slot
    // stored bank (internal linear "func 0x33" encoding, 0-30) to the object-dump bank
    // used in 0x72/0x73/0x76 headers.
    public static int Func33ToObjBank(int type, int idx) => type switch
    {
        1 => idx switch                                  // program: SEVEN internal banks
        {
            >= 0 and <= 6   => idx,                       // I-A..I-G → 0x00..0x06
            7               => 0x10,                      // GM
            >= 8 and <= 17  => 0x10 + (idx - 7),          // g(1)..g(9), g(d) → 0x11..0x1A
            >= 18 and <= 31 => 0x40 + (idx - 18),         // U-A..U-GG → 0x40..0x4D
            _ => -1,
        },
        0 => idx switch                                  // combi: SEVEN internal banks
        {
            >= 0 and <= 6   => idx,                       // I-A..I-G → 0x00..0x06
            >= 7 and <= 13  => 0x40 + (idx - 7),          // U-A..U-G → 0x40..0x46
            _ => -1,
        },
        _ => -1,
    };

    // Object-dump bank number → func-33 bank index. EXACT inverse of Func33ToObjBank;
    // the Librarian uses it to write a new reference (bank byte) into a combi timbre or
    // set-list slot after a move. Validated against real hardware data (99.3% of 500+
    // real set-list references resolve, incl. USER banks). Returns -1 for a bank with no
    // internal-linear representation (never happens for a real stored reference).
    public static int ObjBankToFunc33(int type, int objBank) => type switch
    {
        1 => objBank switch                               // program
        {
            >= 0x00 and <= 0x06 => objBank,               // I-A..I-G → 0..6
            0x10                => 7,                      // GM
            >= 0x11 and <= 0x1A => objBank - 0x10 + 7,    // g(1)..g(d) → 8..17
            >= 0x40 and <= 0x4D => objBank - 0x40 + 18,   // U-A..U-GG → 18..31
            _ => -1,
        },
        0 => objBank switch                               // combi
        {
            >= 0x00 and <= 0x06 => objBank,               // I-A..I-G → 0..6
            >= 0x40 and <= 0x46 => objBank - 0x40 + 7,    // U-A..U-G → 7..13
            _ => -1,
        },
        _ => -1,
    };

    // Object-dump name banks to sweep for a full "Sync Names" (type, objBank).
    //
    // The func-0x77 whole-bank name ENUM (obj 0x13/0x12) is firmware-limited to the
    // PRESET banks (INT, GM/g); it returns Reply code 4 for every USER-writable bank
    // — confirmed on hardware. The sweep handles that split by bank kind: preset banks
    // use the fast 0x77 enum; writable banks (objBank >= 0x40) fall back to a paced
    // per-object func-0x72 fetch (SysExDumpCollector.CollectPerObjectNamesAsync),
    // which DOES work for user banks (128/128 on HW). So every bank here is now
    // self-serve — no front-panel Global→Dump needed. A reject does NOT poison later
    // requests, so order is free; preset banks are listed first so useful names appear
    // immediately while the (slower) per-object user-bank pulls follow.
    public static IEnumerable<(int Type, int ObjBank)> AllNameBanks()
    {
        for (int b = 0x00; b <= 0x06; b++) yield return (1, b);   // program INT   I-A..I-G
        for (int b = 0x00; b <= 0x06; b++) yield return (0, b);   // combi INT     I-A..I-G
        for (int b = 0x10; b <= 0x1A; b++) yield return (1, b);   // program GM/g  (GM+g1-6 dump; g7-gd absent)
        for (int b = 0x40; b <= 0x4D; b++) yield return (1, b);   // program USER  (reject → front-panel dump)
        for (int b = 0x40; b <= 0x46; b++) yield return (0, b);   // combi USER    (reject → front-panel dump)
    }

    // ── Bank Select (msb,lsb) → object-dump bank number ─────────────────────────

    static int ProgramObjBank(int msb, int lsb) => (msb, lsb) switch
    {
        (0x00 or 0x3F, >= 0x00 and <= 0x06) => lsb,             // INT-A..G  → 0..6
        (0x00 or 0x3F, >= 0x08 and <= 0x15) => 0x40 + (lsb - 8),// USER-A..GG→ 0x40..0x4D
        (0x79, 0x00)                        => 0x10,            // GM
        (0x79, >= 0x01 and <= 0x09)         => 0x10 + lsb,      // g(1)..g(9)→ 0x11..0x19
        (0x78, 0x00)                        => 0x1A,            // g(d)
        _ => -1,
    };

    static int CombiObjBank(int msb, int lsb) => (msb, lsb) switch
    {
        (0x00 or 0x3F, >= 0x00 and <= 0x06) => lsb,             // INT-A..G  → 0..6
        (0x00 or 0x3F, >= 0x08 and <= 0x0E) => 0x40 + (lsb - 8),// USER-A..G → 0x40..0x46
        _ => -1,
    };

    // ── Object-dump bank number → display label ─────────────────────────────────

    public static string ProgramLabel(int ob) => ob switch
    {
        >= 0x00 and <= 0x06 => Int(ob),                 // I-A..I-G
        0x10                => "GM",
        >= 0x11 and <= 0x19 => $"g({ob - 0x10})",       // g(1)..g(9)
        0x1A                => "g(d)",
        >= 0x40 and <= 0x4D => User(ob - 0x40),         // U-A..U-GG
        _ => $"?{ob:X2}",
    };

    public static string CombiLabel(int ob) => ob switch
    {
        >= 0x00 and <= 0x06 => Int(ob),                 // I-A..I-G
        >= 0x40 and <= 0x46 => User(ob - 0x40),         // U-A..U-G
        _ => $"?{ob:X2}",
    };

    // Object-dump program banks that can never be a move destination (read-only GM/g).
    public static bool IsReadOnlyProgramBank(int objBank) => objBank is >= 0x10 and <= 0x1A;

    static string Int(int i)  => $"I-{(char)('A' + i)}";
    static string User(int i) => i <= 6
        ? $"U-{(char)('A' + i)}"                        // U-A..U-G
        : $"U-{(char)('A' + i - 7)}{(char)('A' + i - 7)}"; // U-AA..U-GG
}
