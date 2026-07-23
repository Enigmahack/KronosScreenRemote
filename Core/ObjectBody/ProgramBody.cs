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
