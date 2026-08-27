namespace KronosScreenRemote;

// Raw-body accessors for a Wave Sequence object (obj 0x05). Name only - per-step
// decoding (kronosology/docs/interfaces/pcg_file_format.md §7) has no consumer yet.
static class WaveSequenceBody
{
    public static string ReadName(byte[] body) => Librarian.ReadName(body);
}
