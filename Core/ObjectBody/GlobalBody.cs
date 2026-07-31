namespace KronosScreenRemote;

// The user-editable Category / Sub-Category NAMES a Program or Combi's numeric category fields
// point at (requirement 4). Programs and Combis each have 18 categories of 8 sub-categories,
// named independently of each other; a body only ever stores the two NUMBERS
// (ProgramBody/CombiBody.ReadCategory), so without these the Librarian could only ever show
// "Category 5 / Sub-Category 2" where the instrument itself shows "Guitar / Acoustic".
//
// Every array is exactly CategoryCount / SubCategoryCount long and never contains null — a name
// the source didn't provide comes back as the numeric fallback ("Category 05"), so every display
// path can use these directly with no null/short-array handling of its own.
sealed class CategoryNames
{
    public const int CategoryCount = 18;      // 00~0x11, matching the body field's own range
    public const int SubCategoryCount = 8;    // 00~07

    public required string[] Program { get; init; }
    public required string[][] ProgramSub { get; init; }
    public required string[] Combi { get; init; }
    public required string[][] CombiSub { get; init; }

    // The neutral, always-available answer: plain numeric labels, identical in shape to a real
    // decode. Used before anything has ever been synced, and whenever a Global dump isn't
    // available at all (offline) — the display then reads exactly as it did before this feature,
    // which is the point: category NAMES are an enhancement, never a prerequisite.
    public static CategoryNames Numeric() => new()
    {
        Program = BuildNumeric("Category"),
        ProgramSub = BuildNumericSub(),
        Combi = BuildNumeric("Category"),
        CombiSub = BuildNumericSub(),
    };

    // The ONLY way to build one of these from untrusted arrays (a JSON cache file that could be
    // truncated, hand-edited, or written by an older/newer build). Null unless all four are exactly
    // the right shape with no null entries — the caller then falls back to Numeric(). Without this
    // the class's "every array is exactly CategoryCount long" promise is just a comment, and a
    // short ProgramSub would throw on Properties-dialog open rather than degrade.
    public static CategoryNames? TryCreate(string[]? program, string[][]? programSub, string[]? combi, string[][]? combiSub) =>
        ValidFlat(program) && ValidNested(programSub) && ValidFlat(combi) && ValidNested(combiSub)
            ? new CategoryNames { Program = program!, ProgramSub = programSub!, Combi = combi!, CombiSub = combiSub! }
            : null;

    static bool ValidFlat(string[]? names) =>
        names != null && names.Length == CategoryCount && names.All(n => n != null);

    static bool ValidNested(string[][]? names) =>
        names != null && names.Length == CategoryCount && names.All(ValidSub);

    static bool ValidSub(string[]? subs) =>
        subs != null && subs.Length == SubCategoryCount && subs.All(n => n != null);

    static string[] BuildNumeric(string prefix) =>
        Enumerable.Range(0, CategoryCount).Select(i => $"{prefix} {i:D2}").ToArray();

    static string[][] BuildNumericSub() =>
        Enumerable.Range(0, CategoryCount)
            .Select(_ => Enumerable.Range(0, SubCategoryCount).Select(s => $"Sub {s:D2}").ToArray())
            .ToArray();

    public string CategoryLabel(int objType, int category) =>
        InRange(category, CategoryCount) ? (objType == LibObj.Combi ? Combi : Program)[category] : $"Category {category:D2}";

    public string SubCategoryLabel(int objType, int category, int sub) =>
        InRange(category, CategoryCount) && InRange(sub, SubCategoryCount)
            ? (objType == LibObj.Combi ? CombiSub : ProgramSub)[category][sub]
            : $"Sub {sub:D2}";

    static bool InRange(int value, int count) => value >= 0 && value < count;
}

// Decodes the Global object (obj 0x03, bank 0, index 0 — "for all other types bank must be 0",
// KRONOS_MIDI_SysEx.txt *2). Only the category-name block is decoded; the rest of Global (~24 KB
// of tuning, MIDI, controller and scale settings) is out of scope and deliberately untouched.
//
// Offsets read straight off Documentation/MIDI implementation/SysExDumps/Global.txt's own offset
// column, which lays the block out as four contiguous runs: 18 Program category names, then their
// 18x8 sub-category names, then the same two runs for Combi. Every name is a fixed 24-byte ASCII
// field, same convention as an object's own name (Librarian.ReadName).
static class GlobalBody
{
    const int ProgramCategoryOfs    = 12912;   // 18 x 24
    const int ProgramSubCategoryOfs = 13344;   // 18 x 8 x 24
    const int CombiCategoryOfs      = 16800;   // 18 x 24
    const int CombiSubCategoryOfs   = 17232;   // 18 x 8 x 24
    const int NameLength = 24;

    // The last byte this decoder touches — anything shorter isn't a Global body we can read.
    public const int MinimumBodyLength =
        CombiSubCategoryOfs + CategoryNames.CategoryCount * CategoryNames.SubCategoryCount * NameLength;

    // Null when `body` is too short to be a real Global dump (a truncated/rejected reply), so the
    // caller keeps whatever it already had rather than replacing it with garbage.
    public static CategoryNames? ReadCategoryNames(byte[] body)
    {
        if (body.Length < MinimumBodyLength) return null;
        return new CategoryNames
        {
            Program    = ReadRun(body, ProgramCategoryOfs, "Category"),
            ProgramSub = ReadSubRun(body, ProgramSubCategoryOfs),
            Combi      = ReadRun(body, CombiCategoryOfs, "Category"),
            CombiSub   = ReadSubRun(body, CombiSubCategoryOfs),
        };
    }

    static string[] ReadRun(byte[] body, int baseOfs, string fallbackPrefix) =>
        Enumerable.Range(0, CategoryNames.CategoryCount)
            .Select(i => ReadName(body, baseOfs + i * NameLength, $"{fallbackPrefix} {i:D2}"))
            .ToArray();

    static string[][] ReadSubRun(byte[] body, int baseOfs) =>
        Enumerable.Range(0, CategoryNames.CategoryCount)
            .Select(c => Enumerable.Range(0, CategoryNames.SubCategoryCount)
                .Select(s => ReadName(body, baseOfs + (c * CategoryNames.SubCategoryCount + s) * NameLength, $"Sub {s:D2}"))
                .ToArray())
            .ToArray();

    // A blank/whitespace field means "this category was never named" — fall back to the numeric
    // label rather than showing an empty row, so every entry in the dropdown is selectable and
    // tells the user which number it is.
    static string ReadName(byte[] body, int offset, string fallback)
    {
        string name = System.Text.Encoding.ASCII.GetString(body, offset, NameLength).TrimEnd('\0', ' ').Trim();
        return name.Length > 0 ? name : fallback;
    }
}
