namespace KronosScreenRemote;

// Raw-body accessors for a Wave Sequence object (obj 0x05). Name only - per-step
// decoding (kronosology/docs/interfaces/pcg_file_format.md §7) has no consumer yet.
static class WaveSequenceBody
{
    public static string ReadName(byte[] body) => Librarian.ReadName(body);
    public static byte[] WriteName(byte[] body, string name) => Librarian.BuildRenamedBody(body, name);

    public static bool IsInit(byte[] body) => IsInitName(ReadName(body));

    public static bool IsInitName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Contains("INIT", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("WAVE", StringComparison.OrdinalIgnoreCase);
    }
}
