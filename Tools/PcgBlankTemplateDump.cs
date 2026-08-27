namespace KronosScreenRemote;

using System.IO;
using System.Linq;

// Headless diagnostic ONLY (see App.xaml.cs's `--dump-pcg-blanks` flag) - not part of the
// shipped feature set. Finds the real factory-blank body for Drum Kit/Wave Sequence by scanning
// the highest user bank from its last slot backward for a run of byte-identical bodies - an
// untouched slot repeats bit-for-bit, which is stronger evidence than name alone ("Init Drum
// Kit"/"Init Wave Sequence" is also what a user-renamed-back-to-default slot would show).
static class PcgBlankTemplateDump
{
    public static void Run(string pcgPath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(pcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{pcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var file = PcgFile.Open(data);
        if (file == null) { Console.WriteLine($"'{pcgPath}' isn't a recognizable Kronos .pcg file."); Environment.Exit(1); return; }

        Console.WriteLine(pcgPath);
        foreach (var objType in new[] { LibObj.DrumKit, LibObj.WaveSequence })
        {
            var descriptor = ObjectTypeRegistry.Get(objType);
            int highestBank = descriptor.EditableBanks().Max();
            var entries = file.Objects.Where(o => o.Loc.ObjType == objType && o.Loc.Bank == highestBank)
                                       .OrderByDescending(o => o.Loc.Number).ToList();
            Console.WriteLine($"  {descriptor.DisplayName} bank 0x{highestBank:X2} ({descriptor.BankLabel(highestBank)}), highest-to-lowest:");
            if (entries.Count == 0) { Console.WriteLine("    (no entries)"); continue; }

            string tailHash = LocalObjectStore.ComputeHash(entries[0].Body);
            int runLength = 0;
            foreach (var e in entries)
            {
                string hash = LocalObjectStore.ComputeHash(e.Body);
                bool inRun = hash == tailHash;
                if (inRun) runLength++;
                Console.WriteLine($"    [{e.Loc.Number,3}] \"{e.Name}\"  hash={hash[..12]}{(inRun ? "" : "  <- diverges from tail run")}");
                if (!inRun) break;   // first byte-level divergence from the tail - real content starts here
            }

            Console.WriteLine($"    tail run length (byte-identical from the top): {runLength}");
            Console.WriteLine($"    candidate blank name: \"{entries[0].Name}\"  itemSize={entries[0].Body.Length}");
            string outPath = Path.Combine(Path.GetTempPath(), $"kronos_blank_obj{objType:X2}.bin");
            File.WriteAllBytes(outPath, entries[0].Body);
            Console.WriteLine($"    wrote candidate blank bytes to {outPath}");
        }
        Environment.Exit(0);
    }
}
