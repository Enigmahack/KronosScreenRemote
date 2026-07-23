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

    public static byte[] WriteCategory(byte[] body, int category, int subCategory)
    {
        var b = (byte[])body.Clone();
        if (CategoryOfs < b.Length)
            b[CategoryOfs] = (byte)((category & 0x1F) | ((subCategory & 0x07) << 5));
        return b;
    }
}
