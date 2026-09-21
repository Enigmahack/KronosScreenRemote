namespace KronosScreenRemote;

using System.IO;
using System.Linq;
using System.Security.Cryptography;

// Headless diagnostic ONLY (see App.xaml.cs's `--dump-nautilus-bank-digests` flag). Computes,
// per BankIdRaw group in a real Nautilus .pcg file, the same SHA-1 digest the instrument
// itself would report via a live func 0x38 Bank Digest - per NAUTILUS_MIDI_SysEx.txt *38*:
// "generated from the bank data, in the same format as is sent via func 0x73 and 0x75 dumps
// (prior to 8- to 7-bit conversion)... Bank elements are hashed in sequential order."
// NautilusPcgObjectEntry.Body is already that pre-7-bit-conversion raw form, so this is a
// direct concatenate-then-SHA1 of each bank's items' bodies in Index order (no assumption
// about per-bank slot count - Program uses 128, DrumKit/WaveSequence use smaller fixed counts
// that vary by Int-vs-User flavor; whatever count NautilusPcgObjectExtractor actually pulled
// for that bank is hashed, matching what the real device would hash for its own real content).
// Matching this offline-computed digest against a live `--nautilus-sysex-bank-digest-scan`
// result for a given (obj, wire bank byte) is how BankIdRaw gets decoded to a real bank
// identity, without guessing at the raw value's bit layout - see the Program precedent in
// Nautilus Research/MAIN/07_pcg_and_sampling/pcg_format_kronos_vs_nautilus.md.
static class NautilusBankDigestDump
{
    public static void Run(string pcgPath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(pcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{pcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var file = NautilusPcgFile.Open(data);
        if (file == null) { Console.WriteLine($"'{pcgPath}' isn't a recognizable Nautilus .pcg file."); Environment.Exit(1); return; }

        using var sha1 = SHA1.Create();
        foreach (var (objType, label) in new[] { (LibObj.Program, "Program"), (LibObj.DrumKit, "DrumKit"), (LibObj.WaveSequence, "WaveSequence") })
        {
            var objects = file.Objects.Where(o => o.ObjType == objType).ToList();
            Console.WriteLine($"{label}: {objects.Count} object(s)");
            foreach (var g in objects.GroupBy(o => o.BankIdRaw).OrderBy(b => b.Key))
            {
                var ordered = g.OrderBy(o => o.Index).ToList();
                using var stream = new MemoryStream();
                foreach (var o in ordered) stream.Write(o.Body, 0, o.Body.Length);
                string hex = Convert.ToHexString(sha1.ComputeHash(stream.ToArray()));
                Console.WriteLine($"  {label} bankIdRaw=0x{g.Key:X}  count={ordered.Count}  sha1={hex}");
            }
        }

        Environment.Exit(0);
    }
}
