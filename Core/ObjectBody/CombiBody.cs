namespace KronosScreenRemote;

// Raw-body accessors for a Combi object (obj 0x01) — decoded body only, no
// wire-format/8-to-7 knowledge. See ProgramBody for the shared-decoder rationale.
static class CombiBody
{
    // Category/Sub-Category: same bit layout as ProgramBody (bits 4-0 = Category,
    // bits 7-5 = Sub-Category), 12 bytes before LibRefs.Timbre0Num — sits in the
    // Combi Common block immediately before the timbre array (CombiAndSongTimbreSet.txt).
    const int CategoryOfs = 4790;

    public static string ReadName(byte[] body) => Librarian.ReadName(body);
    public static byte[] WriteName(byte[] body, string name) => Librarian.BuildRenamedBody(body, name);

    public static (int Category, int SubCategory) ReadCategory(byte[] body)
    {
        if (CategoryOfs >= body.Length) return (0, 0);
        int packed = body[CategoryOfs];
        return (packed & 0x1F, (packed >> 5) & 0x07);
    }

    // Is this Combi an INIT/placeholder rather than a real combination? See InitObjects for why
    // this matters and why it's detected by shape rather than by a table of known content hashes.
    // Either signal is enough:
    //
    //   • the NAME says so — the instrument's own "Init Combi", and this app's erase output
    //     ("INIT COMBI", Core/LocalLibrary/EraseBody.cs);
    //   • every one of the 16 timbres still points at the zero default (bank 0, program 0). That
    //     is the defining property of an init Combi and the whole reason this check exists: those
    //     references are the encoding of "nothing assigned", not a genuine dependency on Program
    //     I-A:000. A real Combi that used I-A:000 would have to use it in EVERY timbre, with none
    //     of the 16 pointing anywhere else, to be mistaken for one.
    public static bool IsInit(byte[] body) =>
        IsInitName(ReadName(body)) || AllTimbresAtDefault(body);

    public static bool IsInitName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Contains("INIT", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("COMBI", StringComparison.OrdinalIgnoreCase);
    }

    // False for a body too short to hold all 16 timbres (a truncated dump) — IterCombiTimbreRefs
    // stops early there, and "fewer than 16 defaults" must not read as "all defaults".
    static bool AllTimbresAtDefault(byte[] body)
    {
        int seen = 0;
        foreach (var (_, bank, number) in LibRefs.IterCombiTimbreRefs(body))
        {
            if (bank != 0 || number != 0) return false;
            seen++;
        }
        return seen == LibRefs.TimbreCount;
    }

    public static byte[] WriteCategory(byte[] body, int category, int subCategory)
    {
        var b = (byte[])body.Clone();
        if (CategoryOfs < b.Length)
            b[CategoryOfs] = (byte)((category & 0x1F) | ((subCategory & 0x07) << 5));
        return b;
    }
}
