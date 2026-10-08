namespace KronosScreenRemote;

// User-selectable choices for carrying KARMA assignments over to the Nautilus arpeggiator.
// Defaults match the agreed behaviour: a Program's GE goes to Scene 1 only, and a Combi's
// KARMA modules A/B feed ARP A/B with C/D dropped.
public sealed record ArpConversionOptions
{
    // Bit n = Nautilus Scene n+1 (4 Scenes) that receives the converted arp.
    public int ProgramScenes { get; init; } = 0b0001;
    public int CombiScenes { get; init; } = 0b0001;

    // Which Kronos KARMA module (0-3 = A-D, null = none) feeds each Nautilus Combi arpeggiator.
    public int? CombiArpASource { get; init; } = 0;
    public int? CombiArpBSource { get; init; } = 1;

    // Bank-B slots (0-127) transcribed GE patterns may be written to: the Program's (or Combi ARP
    // A's) and Combi ARP B's. Null = don't use transcriptions for that arpeggiator: choosing which
    // user slot to overwrite is the caller's decision.
    public int? TranscribedPatternSlot { get; init; }
    public int? TranscribedPatternSlotB { get; init; }

    // Transcriptions whose simulated playback matched fewer steps than this are not used.
    public double MinTranscriptionScore { get; init; } = 0.9;

    public static readonly ArpConversionOptions Default = new();
}

static class ArpConversion
{
    // Nautilus Program Scene n ARP Pattern Number: u16 LE at 2440 + 28*n. A Combi Scene is 44 bytes
    // and carries two arpeggiators: ARP A at 2440 + 44*n, ARP B 16 bytes after it. Each ARP block:
    // +0 Pattern Number (u16 LE), +2 Octave/Resolution, +3 flags, +4 Gate, +5 Velocity, +6 Swing,
    // +12 Output channel, +13 Input channel.
    public const int SceneBase = 2440, SceneStride = 28, SceneCount = 4;
    public const int CombiSceneStride = 44, CombiArpBOffset = 16;

    // Pattern Number: 0-4 = P0-P4 presets, 5 + i = A i (bank 0x00), 2053 + i = B i (bank 0x40).
    public const int BankAPatternNumberBase = 5, BankBPatternNumberBase = 2053;

    // Kronos GE Number -> Nautilus built-in preset Pattern Number. Hardware-confirmed on both
    // boards (GE names via obj 0x0C, preset labels on the Nautilus ARP page); these are the only
    // GEs with a real Nautilus equivalent - see nautilus_arp_engine_architecture.md section 6.
    static readonly Dictionary<int, int> GeToPreset = new()
    {
        [26] = 0, // Basic Arp UP     -> P0: UP
        [24] = 1, // Basic Arp DOWN   -> P1: DOWN
        [22] = 2, // Basic Arp ALT1   -> P2: ALT1
        [23] = 3, // Basic Arp ALT2   -> P3: ALT2
        [25] = 4, // Basic Arp RANDOM -> P4: RANDOM
    };

    public static int? PresetForGe(int geNumber) => GeToPreset.TryGetValue(geNumber, out var p) ? p : null;

    // How one GE reaches the Nautilus: a preset, a transcribed bank-B pattern, or nothing.
    public sealed record Resolution(int? PatternNumber, int? StepsPerQuarter, (int Slot, byte[] Pattern)? ToStore, string? Warning);

    public static Resolution ResolveGe(int geNumber, KarmaGeCatalog geCatalog, KarmaGePatternLibrary? gePatterns,
        int? slot, double minScore, string where)
    {
        string name = geCatalog.NameForGeNumber(geNumber) is { } n ? $"\"{n}\"" : "(name unknown)";
        if (PresetForGe(geNumber) is int preset)
            return new(preset, null, null, $"KARMA GE #{geNumber} {name} converted to Nautilus preset arp P{preset} in {where}; " +
                "its KARMA realtime parameters are not carried over.");
        if (gePatterns?.ForGe(geNumber) is { } entry && entry.Score >= minScore)
        {
            if (slot is int s and >= 0 and <= 127)
                return new(BankBPatternNumberBase + s, entry.StepsPerQuarter, (s, entry.Pattern),
                    $"KARMA GE #{geNumber} {name} replaced by a transcribed arp pattern (B{s:000}, {entry.Score:P0} of steps match the Kronos, " +
                    $"{(entry.Mode == ArpPatternTranscriber.Encoding.Fixed ? "fixed notes - does not follow the chord played" : "follows the chord played")}) " +
                    $"in {where}; store the pattern to B{s:000} with it.");
            return new(null, null, null, $"KARMA GE #{geNumber} {name} has a transcribed arp pattern ({entry.Score:P0} match) but no bank-B slot " +
                "was chosen, so it was not used.");
        }
        return new(null, null, null, $"KARMA GE #{geNumber} {name} was assigned - Nautilus has no translation for it; " +
            $"assign a Nautilus Arp Pattern to {where} manually if wanted.");
    }

    // Writes one arpeggiator's pattern into every chosen Scene. `arpOffset` is the ARP block's
    // offset within a Scene (0 for a Program or Combi ARP A, CombiArpBOffset for ARP B). A
    // transcription also sets what it assumes: Resolution = its grid, gate 100 (the pattern's own
    // gates) and velocity = Step.
    public static void ApplyToScenes(byte[] body, int sceneMask, int sceneStride, int arpOffset, Resolution r)
    {
        if (r.PatternNumber is not int pn) return;
        for (int n = 0; n < SceneCount; n++)
        {
            if ((sceneMask & (1 << n)) == 0) continue;
            int o = SceneBase + n * sceneStride + arpOffset;
            body[o] = (byte)pn;
            body[o + 1] = (byte)(pn >> 8);
            if (r.StepsPerQuarter is int spq)
            {
                body[o + 2] = (byte)((body[o + 2] & ~0x1C) | ArpPatternTranscriber.ResolutionIndex(spq) << 2);
                body[o + 4] = 100;
                body[o + 5] = 0x81;
            }
        }
    }

    public static void SetProgramScenePattern(byte[] nautilusBody, int sceneMask, int patternNumber) =>
        ApplyToScenes(nautilusBody, sceneMask, SceneStride, 0, new Resolution(patternNumber, null, null, null));

    public static void SetProgramSceneTranscribed(byte[] nautilusBody, int sceneMask, int slot, int stepsPerQuarter) =>
        ApplyToScenes(nautilusBody, sceneMask, SceneStride, 0, new Resolution(BankBPatternNumberBase + slot, stepsPerQuarter, null, null));

    // KARMA Common (Kronos 1304-1306) and Scene Common (Nautilus 1348-1351) sit at the same place in a
    // Program and a Combi: Tempo u16 LE (40.00-300.00 x100, same range) 1304 -> 1350, Latch 1306 bit 6
    // -> 1349 bit 0, KARMA switch 1306 bit 7 -> Arp switch 1349 bit 1. The Arp switch only follows
    // KARMA when an arp was actually assigned; otherwise the template's value stays.
    public const int KronosKarmaTempo = 1304, KronosKarmaSwitches = 1306, NautilusSceneSwitches = 1349, NautilusSceneTempo = 1350;

    public static void CarryKarmaCommon(byte[] kronosBody, byte[] nautilusBody, bool arpAssigned)
    {
        int tempo = kronosBody[KronosKarmaTempo] | kronosBody[KronosKarmaTempo + 1] << 8;
        if (tempo is >= 4000 and <= 30000)
        {
            nautilusBody[NautilusSceneTempo] = (byte)tempo;
            nautilusBody[NautilusSceneTempo + 1] = (byte)(tempo >> 8);
        }
        byte k = kronosBody[KronosKarmaSwitches], s = nautilusBody[NautilusSceneSwitches];
        s = (byte)((s & ~0x01) | ((k >> 6) & 1));
        if (arpAssigned) s = (byte)((s & ~0x02) | (((k >> 7) & 1) << 1));
        nautilusBody[NautilusSceneSwitches] = s;
    }

    public static string SceneList(int sceneMask) =>
        string.Join(", ", Enumerable.Range(0, SceneCount).Where(n => (sceneMask & (1 << n)) != 0).Select(n => $"Scene {n + 1}"));
}
