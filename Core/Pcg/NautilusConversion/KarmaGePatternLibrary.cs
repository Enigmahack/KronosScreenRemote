namespace KronosScreenRemote;

using System.IO;
using System.Reflection;

// Kronos factory GE Number -> transcribed Nautilus Arp Pattern, produced offline by
// `--karma-ge-batch` (render on a real Kronos, ArpPatternTranscriber) and packed with
// `--karma-ge-pack`. Embedded as Resources/KronosKarmaGePatterns.bin; a file of the same name
// beside the exe overrides it, as with KarmaGeCatalog.
//
// File format (all little-endian): "KGPL", u16 version = 1, u16 count, then per entry
// u16 geNumber, u8 encoding (0 = Normal, 1 = Fixed), u8 stepsPerQuarter, u16 score x1000,
// 556-byte pattern.
sealed class KarmaGePatternLibrary
{
    public sealed record Entry(int GeNumber, ArpPatternTranscriber.Encoding Mode, int StepsPerQuarter, double Score, byte[] Pattern);

    const int EntrySize = 2 + 1 + 1 + 2 + ArpPatternTranscriber.PatternSize;
    readonly Dictionary<int, Entry> _byGe = new();

    public int Count => _byGe.Count;
    public Entry? ForGe(int geNumber) => _byGe.GetValueOrDefault(geNumber);

    public static string OverridePath => Path.Combine(Storage.DataDir, "KronosKarmaGePatterns.bin");
    public static readonly KarmaGePatternLibrary Empty = new();

    public static byte[] Pack(IEnumerable<Entry> entries)
    {
        var list = entries.OrderBy(e => e.GeNumber).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("KGPL"u8.ToArray()); w.Write((ushort)1); w.Write((ushort)list.Count);
        foreach (var e in list)
        {
            w.Write((ushort)e.GeNumber); w.Write((byte)e.Mode); w.Write((byte)e.StepsPerQuarter);
            w.Write((ushort)Math.Round(e.Score * 1000)); w.Write(e.Pattern, 0, ArpPatternTranscriber.PatternSize);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static KarmaGePatternLibrary? Parse(byte[] data)
    {
        if (data.Length < 8 || data[0] != 'K' || data[1] != 'G' || data[2] != 'P' || data[3] != 'L' || BitConverter.ToUInt16(data, 4) != 1)
            return null;
        int count = BitConverter.ToUInt16(data, 6);
        if (data.Length < 8 + count * EntrySize) return null;
        var lib = new KarmaGePatternLibrary();
        for (int i = 0; i < count; i++)
        {
            int o = 8 + i * EntrySize;
            var pattern = data.AsSpan(o + 6, ArpPatternTranscriber.PatternSize).ToArray();
            lib._byGe[BitConverter.ToUInt16(data, o)] = new Entry(BitConverter.ToUInt16(data, o), (ArpPatternTranscriber.Encoding)data[o + 2],
                data[o + 3], BitConverter.ToUInt16(data, o + 4) / 1000.0, pattern);
        }
        return lib;
    }

    public static KarmaGePatternLibrary FromCatalog()
    {
        try
        {
            if (File.Exists(OverridePath) && Parse(File.ReadAllBytes(OverridePath)) is { } fromFile) return fromFile;
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".KronosKarmaGePatterns.bin", StringComparison.Ordinal));
            if (name == null) return Empty;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return Empty;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Parse(ms.ToArray()) ?? Empty;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[karma-ge-patterns] load failed: {ex.Message}");
            return Empty;
        }
    }
}
