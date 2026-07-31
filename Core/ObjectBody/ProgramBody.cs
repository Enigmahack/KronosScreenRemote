namespace KronosScreenRemote;

// Raw-body accessors for a Program object (obj 0x00) — operates on the decoded
// binary body only, no wire-format/8-to-7 knowledge. Shared by the live-dump path
// and the .pcg-file path (Core/Pcg): a wire-format HD-1 body is an exact prefix of the
// larger .pcg on-disk slot (see ProgramFormatConverter/PcgObjectExtractor for the confirmed
// byte-level relationship), and every offset this class reads (name at 0, category at 2568)
// falls within that shared prefix — so both paths can use it identically regardless of size.
static class ProgramBody
{
    // Category/Sub-Category: bits 4-0 = Category (00~0x11), bits 7-5 = Sub-Category
    // (00~07). Offset confirmed byte-identical in both Prog_HD-1.txt and
    // Prog_EXi_Common.txt (the shared "Common" parameter block), so one offset
    // covers both program bank types.
    const int CategoryOfs = 2568;

    public static string ReadName(byte[] body) => Librarian.ReadName(body);
    public static byte[] WriteName(byte[] body, string name) => Librarian.BuildRenamedBody(body, name);

    public static (int Category, int SubCategory) ReadCategory(byte[] body)
    {
        if (CategoryOfs >= body.Length) return (0, 0);
        int packed = body[CategoryOfs];
        return (packed & 0x1F, (packed >> 5) & 0x07);
    }

    // Is this Program an INIT/placeholder rather than a real patch? Both wire formats name
    // theirs with some spelling of "Init … Program" ("Init Program", "Init EXi Program"), and
    // this app's own erase path writes "INIT PROGRAM" (Core/LocalLibrary/EraseBody.cs) — so a
    // case-insensitive "contains INIT and PROGRAM" catches the hardware's naming and ours alike,
    // without hardcoding a single exact string that a future OS revision could change.
    //
    // The NAME is the only thing that can answer this: a Kronos slot is never empty (the protocol
    // has no "delete" — see EraseBody's own comment), so an unused slot holds a full, valid INIT
    // body whose bytes are otherwise indistinguishable from a real patch's. That's exactly what
    // the user sees in the Librarian, and what the placement gate keys off (BatchLibrarian.
    // PlanBatchMove's orphan gate): overwriting a slot whose occupant is merely INIT destroys
    // nothing, so it must not demand a Force Overwrite the way a real referenced patch does.
    public static bool IsInit(byte[] body) => IsInitName(ReadName(body));

    // Name-only overload, for callers that already hold the decoded display name and must not
    // pay a blob read to answer this (LocalLibraryCache.GetDisplayName is cached at write time —
    // see LocalIndexEntry's own comment).
    public static bool IsInitName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Contains("INIT", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("PROGRAM", StringComparison.OrdinalIgnoreCase);
    }

    // Same bytes as `body`, only the Category/Sub-Category byte replaced — every
    // other byte preserved exactly (same discipline as Librarian.BuildRenamedBody).
    public static byte[] WriteCategory(byte[] body, int category, int subCategory)
    {
        var b = (byte[])body.Clone();
        if (CategoryOfs < b.Length)
            b[CategoryOfs] = (byte)((category & 0x1F) | ((subCategory & 0x07) << 5));
        return b;
    }
}
