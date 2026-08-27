namespace KronosScreenRemote;

// Raw-body accessors for a Drum Kit object (obj 0x04). Name only - per-zone/per-note
// decoding (kronosology/docs/interfaces/pcg_file_format.md §6) has no consumer yet.
static class DrumKitBody
{
    public static string ReadName(byte[] body) => Librarian.ReadName(body);
    public static byte[] WriteName(byte[] body, string name) => Librarian.BuildRenamedBody(body, name);

    public static bool IsInit(byte[] body) => IsInitName(ReadName(body));

    public static bool IsInitName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Contains("INIT", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("DRUM", StringComparison.OrdinalIgnoreCase);
    }
}
