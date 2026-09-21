namespace KronosScreenRemote;

using System.IO;
using System.Linq;

// Headless diagnostic ONLY (see App.xaml.cs's `--dump-nautilus-pcg-structure` flag) - not part
// of the shipped feature set. Written to validate NautilusPcgObjectExtractor against real
// Nautilus-written .pcg files (FACTORY/PRELOAD.PCG, PCG Info/ZacBak/Sept8_2026.PCG), the same
// way PcgStructureDump validated the Kronos-side extractor - see the PCG converter v1 plan's
// Phase 2. Since NautilusPcgObjectEntry exposes BankIdRaw undecoded (no semantic bank/slot
// resolved yet), this groups by that raw value rather than a decoded bank label.
static class NautilusPcgStructureDump
{
    public static void Run(string pcgPath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(pcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{pcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var file = NautilusPcgFile.Open(data);
        if (file == null) { Console.WriteLine($"'{pcgPath}' isn't a recognizable Nautilus .pcg file."); Environment.Exit(1); return; }

        Console.WriteLine($"{pcgPath}  ({data.Length} bytes)");
        foreach (var g in file.Objects.GroupBy(o => o.ObjType).OrderBy(g => g.Key))
        {
            string typeName = g.Key switch
            {
                LibObj.Program => "Program", LibObj.DrumKit => "DrumKit", LibObj.WaveSequence => "WaveSequence",
                _ => $"obj0x{g.Key:X2}",
            };
            var byBank = g.GroupBy(o => o.BankIdRaw).OrderBy(b => b.Key).ToList();
            Console.WriteLine($"  {typeName}: {g.Count()} objects across {byBank.Count} bank(s)");
            foreach (var b in byBank)
                Console.WriteLine($"    bankIdRaw=0x{b.Key:X}: {b.Count()} record(s), itemSize={b.First().Body.Length}, sample names: " +
                    string.Join(" | ", b.Where(o => o.Name.Trim().Length > 0).Take(3).Select(o => $"[{o.Index}]\"{o.Name}\"")));
        }

        // Sanity-check that DrumKitBody.IterSampleZoneRefs's Kronos-derived offsets
        // (NoteBase=24, NoteStride=300, ZoneStride=34, etc.) produce plausible output against
        // REAL Nautilus bytes, not just synthetic fixtures - see the PCG converter v1 plan's
        // Phase 3a DrumKit follow-up. Prints the first few classified zone refs of the first
        // populated real (non-init) Drum Kit record found.
        var realKit = file.Objects.FirstOrDefault(o => o.ObjType == LibObj.DrumKit && !o.Name.StartsWith("Init", StringComparison.OrdinalIgnoreCase));
        if (realKit != null)
        {
            Console.WriteLine($"  Drum Kit zone sanity check on \"{realKit.Name}\":");
            int shown = 0;
            foreach (var (note, zone, uuid, sampleId) in DrumKitBody.IterSampleZoneRefs(realKit.Body))
            {
                var c = SampleReferenceWalker.Classify(uuid);
                Console.WriteLine($"    note={note} zone={zone} class={c.Class} legacyExs={c.LegacyExsNumber} rawUuid={c.RawUuidHex} sampleId={sampleId}");
                if (++shown >= 12) break;
            }
            if (shown == 0) Console.WriteLine("    (no populated zones found in this record)");
        }

        // Same sanity check as above, for WaveSequenceBody.IterSampleStepRefs (StepBase=40,
        // StepStride=34, etc.) against a real Nautilus Wave Sequence record.
        var realWaveSeq = file.Objects.FirstOrDefault(o => o.ObjType == LibObj.WaveSequence && !o.Name.StartsWith("Init", StringComparison.OrdinalIgnoreCase));
        if (realWaveSeq != null)
        {
            Console.WriteLine($"  Wave Sequence step sanity check on \"{realWaveSeq.Name}\":");
            int shownWs = 0;
            foreach (var (step, uuid, select) in WaveSequenceBody.IterSampleStepRefs(realWaveSeq.Body))
            {
                var c = SampleReferenceWalker.Classify(uuid);
                Console.WriteLine($"    step={step} class={c.Class} legacyExs={c.LegacyExsNumber} rawUuid={c.RawUuidHex} select={select}");
                if (++shownWs >= 12) break;
            }
            if (shownWs == 0) Console.WriteLine("    (no populated steps found in this record)");
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
