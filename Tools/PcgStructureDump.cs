namespace KronosScreenRemote;

using System.IO;
using System.Linq;

// Headless diagnostic ONLY (see App.xaml.cs's `--dump-pcg-structure` flag) - not part of the
// shipped feature set. Written to validate PcgObjectExtractor's newly-added DBK1/WBK1/GLB1
// handling (Drum Kit/Wave Sequence/Global) against real hardware-written .pcg files, the same
// way PcgRefDump settled the Combi-timbre-reference question with real bytes instead of doc
// reasoning alone.
static class PcgStructureDump
{
    public static void Run(string pcgPath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(pcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{pcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var file = PcgFile.Open(data);
        if (file == null) { Console.WriteLine($"'{pcgPath}' isn't a recognizable Kronos .pcg file."); Environment.Exit(1); return; }

        Console.WriteLine($"{pcgPath}  ({data.Length} bytes)");
        foreach (var g in file.Objects.GroupBy(o => o.Loc.ObjType).OrderBy(g => g.Key))
        {
            string typeName = g.Key switch
            {
                LibObj.Program => "Program", LibObj.Combi => "Combi", LibObj.SetList => "SetList",
                LibObj.DrumKit => "DrumKit", LibObj.WaveSequence => "WaveSequence", LibObj.Global => "Global",
                _ => $"obj0x{g.Key:X2}",
            };
            var byBank = g.GroupBy(o => o.Loc.Bank).OrderBy(b => b.Key).ToList();
            Console.WriteLine($"  {typeName}: {g.Count()} objects across {byBank.Count} bank(s)");
            foreach (var b in byBank)
                Console.WriteLine($"    bank 0x{b.Key:X2}: {b.Count()} record(s), itemSize={b.First().Body.Length}, sample names: " +
                    string.Join(" | ", b.Where(o => o.Name.Trim().Length > 0).Take(3).Select(o => $"[{o.Loc.Number}]\"{o.Name}\"")));
        }

        if (file.RejectedBanks.Count > 0)
        {
            Console.WriteLine("  REJECTED:");
            foreach (var r in file.RejectedBanks)
                Console.WriteLine($"    {r.Tag} @0x{r.Offset:X} count={r.Count} itemSize={r.ItemSize} bankIdRaw=0x{r.BankIdRaw:X} - {r.Reason}");
        }
        if (file.ChecksumWarnings.Count > 0)
        {
            Console.WriteLine("  CHECKSUM WARNINGS:");
            foreach (var w in file.ChecksumWarnings)
                Console.WriteLine($"    {w.Tag} @0x{w.Offset:X} expected=0x{w.Expected:X2} actual=0x{w.Actual:X2}");
        }
        Environment.Exit(0);
    }
}
