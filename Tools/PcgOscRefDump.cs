namespace KronosScreenRemote;

using System.IO;
using System.Linq;
using System.Text;

// Headless diagnostic ONLY (see App.xaml.cs's `--dump-pcg-osc-refs` flag) - not part of the
// shipped feature set. Written to settle whether an HD-1 Program's OSC1 Zone1 "MS Bank
// UUID"/"MS Number" fields (Prog_HD-1.txt offsets 2775/2792) hold a real sample-bank UUID
// (MS Type=Multisample) or a small Wave Sequence/Drum Kit bank+number pair (MS Type=Wave
// Sequence, or Oscillator Mode=Drums) - undocumented in the text SysEx dump, so this settles
// it against real hardware-written bytes instead of guessing.
static class PcgOscRefDump
{
    public static void Run(string pcgPath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(pcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{pcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var file = PcgFile.Open(data);
        if (file == null) { Console.WriteLine($"'{pcgPath}' isn't a recognizable Kronos .pcg file."); Environment.Exit(1); return; }

        Console.WriteLine(pcgPath);
        int shown = 0;
        foreach (var e in file.Objects.Where(o => o.Loc.ObjType == LibObj.Program && !o.IsExi))
        {
            if (e.Body.Length < 2794) continue;
            int oscMode = e.Body[2558] & 0x07;
            int zone1MsType = e.Body[2774] & 0x03;
            bool interesting = zone1MsType == 2 || oscMode is 4 or 5;
            if (!interesting) continue;

            var uuid = e.Body[2775..2791];
            int msNumber = e.Body[2792] | (e.Body[2793] << 8);   // LE, per §7's established convention
            int msNumberBE = (e.Body[2792] << 8) | e.Body[2793];
            Console.WriteLine($"  {e.Loc.Label()} \"{e.Name}\"  oscMode={oscMode}  zone1MsType={zone1MsType}  " +
                $"uuid={Convert.ToHexString(uuid)}  msNumberLE={msNumber}  msNumberBE={msNumberBE}");
            shown++;
        }
        if (shown == 0) Console.WriteLine("  no HD-1 Program with MS Type=2 or Oscillator Mode=4/5 found in this file.");
        Environment.Exit(0);
    }
}
