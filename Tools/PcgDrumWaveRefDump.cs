namespace KronosScreenRemote;

using System.IO;
using System.Linq;

// Headless diagnostic ONLY (see App.xaml.cs's `--dump-pcg-drumwave-refs` flag) - not part of the
// shipped feature set. Follow-up to PcgOscRefDump: that tool found the HD-1 zone MS Number field
// carries index values that don't fit as raw external-sample indices for Wave-Sequence-typed
// zones, nor for Multisample-typed zones under Oscillator Mode=Drums. KRONOS_MIDI_SysEx.txt
// [0x71] "Set Current Object" documents the actual answer: Drum Kit/Wave Seq use LINEAR
// addressing (Int then User A..G, then - for Drum Kit only - GM, then User AA..GG) instead of
// bank+id, and that's explicitly "the same means of addressing... used by the HD-1 MS number
// parameter." This tool converts the linear index per that table and cross-checks the result
// against the real DKT1/WBK1 catalog names present in the same file.
static class PcgDrumWaveRefDump
{
    static (int Bank, int Slot)? DrumKitLinearToLoc(int linear) => linear switch
    {
        >= 0 and <= 39     => (0, linear),
        >= 40 and <= 151   => (0x40 + (linear - 40) / 16, (linear - 40) % 16),
        >= 152 and <= 160  => (0x10, linear - 152),
        >= 161 and <= 272  => (0x47 + (linear - 161) / 16, (linear - 161) % 16),
        _ => null,
    };

    static (int Bank, int Slot)? WaveSeqLinearToLoc(int linear) => linear switch
    {
        >= 0 and <= 149    => (0, linear),
        >= 150 and <= 373  => (0x40 + (linear - 150) / 32, (linear - 150) % 32),
        >= 374 and <= 597  => (0x47 + (linear - 374) / 32, (linear - 374) % 32),
        _ => null,
    };

    public static void Run(string pcgPath, string nameFilter)
    {
        byte[] data;
        try { data = File.ReadAllBytes(pcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{pcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var file = PcgFile.Open(data);
        if (file == null) { Console.WriteLine($"'{pcgPath}' isn't a recognizable Kronos .pcg file."); Environment.Exit(1); return; }

        var drumKits = file.Objects.Where(o => o.Loc.ObjType == LibObj.DrumKit).ToDictionary(o => (o.Loc.Bank, o.Loc.Number), o => o.Name);
        var waveSeqs = file.Objects.Where(o => o.Loc.ObjType == LibObj.WaveSequence).ToDictionary(o => (o.Loc.Bank, o.Loc.Number), o => o.Name);

        Console.WriteLine(pcgPath);
        int shown = 0;
        foreach (var e in file.Objects.Where(o => o.Loc.ObjType == LibObj.Program && !o.IsExi
                                                    && o.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (e.Body.Length < 3440) continue;
            int oscMode = e.Body[2558] & 0x07;
            Console.WriteLine($"  {e.Loc.Label()} \"{e.Name}\"  oscillatorMode={oscMode}");

            void DumpOsc(string label, int typeBase, int uuidBase, int numBase)
            {
                for (int zone = 0; zone < 8; zone++)
                {
                    int typeOff = typeBase + zone * 22;
                    int uuidOff = uuidBase + zone * 22;
                    int numOff = numBase + zone * 22;
                    int msType = e.Body[typeOff] & 0x03;
                    if (msType == 0) continue; // Off

                    var uuid = e.Body[uuidOff..(uuidOff + 16)];
                    int number = e.Body[numOff] | (e.Body[numOff + 1] << 8);

                    string interp;
                    if (msType == 2)
                    {
                        var loc = WaveSeqLinearToLoc(number);
                        string name = loc != null && waveSeqs.TryGetValue(loc.Value, out var n) ? $"\"{n}\"" : "(no matching WBK1 entry in this file)";
                        interp = loc != null ? $"WaveSeq linear={number} -> bank=0x{loc.Value.Bank:X2} slot={loc.Value.Slot:D3} {name}" : $"WaveSeq linear={number} OUT OF RANGE";
                    }
                    else if (msType == 1 && oscMode is 4 or 5)
                    {
                        var loc = DrumKitLinearToLoc(number);
                        string name = loc != null && drumKits.TryGetValue(loc.Value, out var n) ? $"\"{n}\"" : "(no matching DBK1 entry in this file)";
                        interp = loc != null ? $"DrumKit linear={number} -> bank=0x{loc.Value.Bank:X2} slot={loc.Value.Slot:D3} {name}" : $"DrumKit linear={number} OUT OF RANGE";
                    }
                    else
                    {
                        interp = $"raw multisample, number={number}";
                    }
                    Console.WriteLine($"    {label} Zone{zone + 1}: msType={msType} uuid={Convert.ToHexString(uuid)} {interp}");
                }
            }

            DumpOsc("OSC1", 2774, 2775, 2792);
            DumpOsc("OSC2", 3240, 3241, 3258);

            int dtProgNum = e.Body[2688];
            int dtProgBank = e.Body[2689];
            if (dtProgBank != 0 || dtProgNum != 0)
                Console.WriteLine($"    DrumTrack: programBank=0x{dtProgBank:X2} programNumber={dtProgNum}");

            shown++;
        }
        if (shown == 0) Console.WriteLine($"  no HD-1 Program matching \"{nameFilter}\" found in this file.");
        Environment.Exit(0);
    }
}
