namespace KronosScreenRemote;

using System.Text;

// Top-level Nautilus .pcg file validation + extraction entry point. Mirrors PcgFile
// (Core/Pcg/PcgFile.cs) but for the Nautilus model byte instead of Kronos's - kept as a
// separate class rather than a flag on PcgFile because the object-level extraction it
// delegates to (NautilusPcgObjectExtractor) does NOT share Kronos's per-object-type
// bank-id decoding (see that class for why). Read-only, in-memory, same as PcgFile.
//
// Container/chunk/checksum layer is confirmed byte-identical to Kronos - only the model
// byte differs (offset 4: Kronos 0x68, Nautilus 0xDD) - verified against 3 real files
// (2 independent Nautilus exports + Kronos ground truth) via
// Nautilus Research/scripts/pcg_truth_nautilus.py, a direct port of Kronos's own
// pcg_truth.py that needed zero changes to the walker itself. See
// Nautilus Research/MAIN/07_pcg_and_sampling/pcg_format_kronos_vs_nautilus.md.
sealed class NautilusPcgFile
{
    public IReadOnlyList<NautilusPcgObjectEntry> Objects { get; }
    public IReadOnlyList<PcgRejectedBank> RejectedBanks { get; }
    public IReadOnlyList<PcgChecksumWarning> ChecksumWarnings { get; }

    NautilusPcgFile(List<NautilusPcgObjectEntry> objects, List<PcgRejectedBank> rejectedBanks, List<PcgChecksumWarning> checksumWarnings)
    {
        Objects = objects;
        RejectedBanks = rejectedBanks;
        ChecksumWarnings = checksumWarnings;
    }

    // Returns null if `data` isn't a recognizable Nautilus .pcg file, same convention as
    // PcgFile.Open (a malformed or wrong-platform file is an expected input, not a bug).
    public static NautilusPcgFile? Open(byte[] data)
    {
        if (data.Length < 16) return null;
        if (Encoding.ASCII.GetString(data, 0, 4) != "KORG") return null;
        if (data[4] != 0xDD) return null;   // Product ID: Nautilus (0x68 is Kronos - use PcgFile for that)
        if (data[5] != 0x00) return null;   // File type: 00 = PCG (01 = SNG - out of scope)
        var objects = NautilusPcgObjectExtractor.Extract(data, out var rejected, out var checksumWarnings);
        return new NautilusPcgFile(objects, rejected, checksumWarnings);
    }
}
