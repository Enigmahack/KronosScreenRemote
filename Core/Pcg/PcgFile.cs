namespace KronosScreenRemote;

using System.Text;

// Top-level .pcg file validation + extraction entry point. Read-only, in-memory (the
// caller loads bytes however it wants — local file, or downloaded via FTP into a temp
// cache, per requirement 2 — this class never touches disk itself).
sealed class PcgFile
{
    public IReadOnlyList<PcgObjectEntry> Objects { get; }
    public IReadOnlyList<PcgRejectedBank> RejectedBanks { get; }

    PcgFile(List<PcgObjectEntry> objects, List<PcgRejectedBank> rejectedBanks)
    {
        Objects = objects;
        RejectedBanks = rejectedBanks;
    }

    // Returns null if `data` isn't a recognizable Kronos .pcg file (bad magic/product id/
    // file type) rather than throwing — a malformed or unrelated file is an expected input
    // from a "Load PCG..." file picker, not a bug.
    public static PcgFile? Open(byte[] data)
    {
        if (data.Length < 16) return null;
        if (Encoding.ASCII.GetString(data, 0, 4) != "KORG") return null;
        if (data[4] != 0x68) return null;   // Product ID: Kronos (other Korg models use a different id — out of scope)
        if (data[5] != 0x00) return null;   // File type: 00 = PCG (01 = SNG — Songs are out of scope, requirement 3)
        var objects = PcgObjectExtractor.Extract(data, out var rejected);
        return new PcgFile(objects, rejected);
    }
}
