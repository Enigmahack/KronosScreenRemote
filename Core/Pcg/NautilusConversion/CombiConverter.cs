namespace KronosScreenRemote;

// Kronos -> Nautilus Combination body converter. Layout per a field-by-field diff of both platforms'
// CombiAndSongTimbreSet.txt (scripts/diff_sysex_docs.py, pcg_format_kronos_vs_nautilus.md):
//  - [0, 1268) is shared (the Vector Control -> X-Y Control rename keeps its offsets).
//  - Kronos [1268, 4790) (audio inputs, drum track, KARMA Common + Modules A-D) is replaced on the
//    Nautilus by [1268, 2620) (audio USB, Scene Common, Step Seq, 4 Scenes with ARP A/B + drum).
//    That block comes from `nautilusTemplate`, as for ProgramConverter.
//  - From Common/Category (Kronos 4790, Nautilus 2620) to the end everything is shifted by -2170,
//    including the 16 x 188-byte Timbres; only a few Kronos-only bits in it are cleared.
// Timbre Program references (bank/number) name Nautilus slots after conversion. By default they are
// re-pointed at the Nautilus factory equivalent of the Kronos factory Program (ProgramRefMap);
// references the map doesn't cover keep their slot numbers, so those Programs have to be converted
// and placed at the same Nautilus slots.
public enum TimbreProgramRefs { FactoryEquivalents, KeepSlots }

static class CombiConverter
{
    public sealed record ConversionResult(byte[]? NautilusBody, List<string> Warnings, List<string> Refusals,
        List<(int Slot, byte[] Pattern)> ArpPatternsToStore);

    public const int KronosSize = 7810, NautilusSize = 5640;
    public const int SharedHeadEnd = 1268, KronosTailStart = 4790, NautilusTailStart = 2620;
    public const int SizeDelta = KronosTailStart - NautilusTailStart;   // 2170

    // Kronos KARMA Module m (A-D): 1814 + 744*m. +0 GE Number (u16 LE), +2 bits 4-0 Input channel,
    // +3 Output channel, +5 Key Zone Top, +6 Key Zone Bottom.
    const int KarmaModuleBase = 1814, KarmaModuleStride = 744;
    // Nautilus Scene Common ARP A/B zones: Key Top, Key Bottom, Velocity Top, Velocity Bottom.
    const int NautilusArpAZones = 2612, NautilusArpBZones = 2616;

    // Assignable Switch 1/2 + Knob 5-8 (Nautilus 2626-2631): the doc lists only Switch Status there, but
    // Korg's own factory Combis carry values in all six bytes with a different enumeration than the
    // Kronos (e.g. Kronos 3,13,16,18,3,4 -> Nautilus 1,1,1,2,3,4 for "Premium Piano & Strings"), so
    // they come from the Nautilus template like the rest of the Nautilus-owned block.
    const int NautilusAssignStart = 2626, NautilusAssignEnd = 2631;
    const int TimbreBase = 2632, TimbreStride = 188, TimbreCount = 16;
    // Per Timbre: +30 Chord Source (bits 6-3), +31 Chord Mode (bits 6-5), +35 (Filter) Waveform SysEx
    // (bit 7). The (Filter) Knob byte (+33) is kept whole: the doc shows no Nautilus bits 4-5, but Korg's
    // factory Combis keep them set exactly as on the Kronos.
    static readonly (int Offset, byte Mask, string What)[] TimbreKronosOnlyBits =
    {
        (30, 0b0111_1000, "Chord Source"),
        (31, 0b0110_0000, "Chord Mode"),
        (35, 0b1000_0000, "(Filter) Waveform SysEx"),
    };

    // `kronosProgramName` (optional) names the Program actually stored at a Kronos (bank code, index)
    // in the source set; a reference is only re-pointed when that name still matches the factory
    // Program the map was built from. Without it the source slots are assumed to hold factory content.
    public static ConversionResult KronosToNautilus(byte[] kronosBody, KarmaGeCatalog geCatalog, byte[]? nautilusTemplate = null,
        ArpConversionOptions? arpOptions = null, KarmaGePatternLibrary? gePatterns = null,
        TimbreProgramRefs programRefs = TimbreProgramRefs.FactoryEquivalents, ProgramRefMap? refMap = null,
        Func<int, int, string?>? kronosProgramName = null)
    {
        var warnings = new List<string>();
        var refusals = new List<string>();
        var toStore = new List<(int, byte[])>();
        arpOptions ??= ArpConversionOptions.Default;

        if (kronosBody.Length != KronosSize)
            refusals.Add($"Kronos Combi body is {kronosBody.Length} bytes, expected {KronosSize}.");
        if (nautilusTemplate != null && nautilusTemplate.Length != NautilusSize)
            refusals.Add($"Nautilus template Combi is {nautilusTemplate.Length} bytes, expected {NautilusSize}.");
        if (refusals.Count > 0) return new ConversionResult(null, warnings, refusals, toStore);
        if (nautilusTemplate == null)
            warnings.Add("No Nautilus template Combi supplied - the Nautilus-only Scene/ARP/Step Seq/audio settings were zero-filled.");

        var body = new byte[NautilusSize];
        Array.Copy(kronosBody, 0, body, 0, SharedHeadEnd);
        if (nautilusTemplate != null)
            Array.Copy(nautilusTemplate, SharedHeadEnd, body, SharedHeadEnd, NautilusTailStart - SharedHeadEnd);
        Array.Copy(kronosBody, KronosTailStart, body, NautilusTailStart, KronosSize - KronosTailStart);

        // KARMA modules -> ARP A/B.
        var used = new HashSet<int>();
        bool assigned = false;
        foreach (var (source, arpOffset, zones, slot, label) in new[]
        {
            (arpOptions.CombiArpASource, 0, NautilusArpAZones, arpOptions.TranscribedPatternSlot, "ARP A"),
            (arpOptions.CombiArpBSource, ArpConversion.CombiArpBOffset, NautilusArpBZones, arpOptions.TranscribedPatternSlotB, "ARP B"),
        })
        {
            if (source is not int m || m is < 0 or > 3) continue;
            used.Add(m);
            int k = KarmaModuleBase + m * KarmaModuleStride;
            int ge = kronosBody[k] | kronosBody[k + 1] << 8;
            if (ge == 0) continue;   // treated as unused, as for Programs
            string where = $"{label} ({ArpConversion.SceneList(arpOptions.CombiScenes)})";
            var r = ArpConversion.ResolveGe(ge, geCatalog, gePatterns, slot, arpOptions.MinTranscriptionScore, where);
            if (arpOptions.CombiScenes == 0 && r.PatternNumber != null)
                r = r with { PatternNumber = null, ToStore = null, Warning = $"KARMA Module {(char)('A' + m)} GE #{ge} was not converted: no Scene was chosen." };
            if (r.Warning != null) warnings.Add($"Module {(char)('A' + m)}: {r.Warning}");
            if (r.PatternNumber == null) continue;

            assigned = true;
            ArpConversion.ApplyToScenes(body, arpOptions.CombiScenes, ArpConversion.CombiSceneStride, arpOffset, r);
            for (int n = 0; n < ArpConversion.SceneCount; n++)
            {
                if ((arpOptions.CombiScenes & (1 << n)) == 0) continue;
                int o = ArpConversion.SceneBase + n * ArpConversion.CombiSceneStride + arpOffset;
                body[o + 12] = kronosBody[k + 3];                 // output channel: the timbres KARMA was driving
                body[o + 13] = (byte)(kronosBody[k + 2] & 0x1F);  // input channel
            }
            body[zones] = kronosBody[k + 5];
            body[zones + 1] = kronosBody[k + 6];
            if (r.ToStore is { } p) toStore.Add(p);
        }
        if (toStore.Count == 2 && toStore[0].Item1 == toStore[1].Item1)
            refusals.Add($"ARP A and ARP B were given the same bank-B slot (B{toStore[0].Item1:000}).");

        foreach (int m in Enumerable.Range(0, 4).Where(m => !used.Contains(m)))
        {
            int k = KarmaModuleBase + m * KarmaModuleStride;
            int ge = kronosBody[k] | kronosBody[k + 1] << 8;
            if (ge != 0)
                warnings.Add($"KARMA Module {(char)('A' + m)} (GE #{ge} \"{geCatalog.NameForGeNumber(ge) ?? "?"}\") was dropped - the Nautilus has two arpeggiators.");
        }
        ArpConversion.CarryKarmaCommon(kronosBody, body, assigned);

        // Kronos-only bits inside the shifted tail.
        bool Clear(int offset, byte mask) { bool had = (body[offset] & mask) != 0; body[offset] &= (byte)~mask; return had; }
        var cleared = new List<string>();
        for (int i = NautilusAssignStart; i <= NautilusAssignEnd; i++) body[i] = nautilusTemplate?[i] ?? 0;
        cleared.Add("Assignable Switch 1/2 and Knob 5-8 assignments (Nautilus values kept)");
        foreach (var (off, mask, what) in TimbreKronosOnlyBits)
        {
            bool any = false;
            for (int t = 0; t < TimbreCount; t++) any |= Clear(TimbreBase + t * TimbreStride + off, mask);
            if (any) cleared.Add($"Timbre {what}");
        }
        if (cleared.Count > 0)
            warnings.Add($"Kronos-only settings removed: {string.Join(", ", cleared)}.");
        warnings.Add("KARMA performance data (Realtime Controls, Dynamic MIDI, Scenes), Drum Track and audio input settings were not carried over.");
        RemapTimbrePrograms(body, programRefs, refMap, kronosProgramName, warnings);

        return new ConversionResult(refusals.Count > 0 ? null : body, warnings, refusals, toStore);
    }

    static void RemapTimbrePrograms(byte[] body, TimbreProgramRefs mode, ProgramRefMap? refMap,
        Func<int, int, string?>? kronosProgramName, List<string> warnings)
    {
        if (mode == TimbreProgramRefs.KeepSlots || refMap == null || refMap.Count == 0)
        {
            warnings.Add("Timbre Program references were kept as-is; the referenced Programs must be converted and placed at the same Nautilus slots.");
            return;
        }
        var remapped = new List<string>();
        var kept = new List<(int Timbre, string Ref)>();
        for (int t = 0; t < TimbreCount; t++)
        {
            int o = TimbreBase + t * TimbreStride;
            if (body[o + 2] >> 5 == 0) continue;   // Status Off: not played, leave it alone
            int index = body[o], bank = body[o + 1];
            var e = refMap.For(bank, index);
            string? actual = kronosProgramName?.Invoke(bank, index);
            if (e == null || (actual != null && !string.Equals(actual.Trim(), e.KronosName.Trim(), StringComparison.Ordinal)))
            {
                kept.Add((t + 1, $"{ProgramRefMap.KronosLabel(bank, index)}{(actual != null ? $" \"{actual.Trim()}\"" : "")}"));
                continue;
            }
            body[o] = (byte)e.NautilusIndex;
            body[o + 1] = (byte)e.NautilusBank;
            remapped.Add($"{t + 1}: {ProgramRefMap.KronosLabel(bank, index)} \"{e.KronosName}\" -> {ProgramRefMap.NautilusLabel(e.NautilusBank, e.NautilusIndex)} " +
                $"\"{e.NautilusName}\"{(e.Source == "korg" ? " (as in Korg's Nautilus factory Combis)" : "")}");
        }
        if (remapped.Count > 0)
            warnings.Add($"Timbre Programs re-pointed to Nautilus factory equivalents: {string.Join("; ", remapped)}.");
        if (kept.Count > 0)
            warnings.Add($"{string.Join("; ", kept.GroupBy(k => k.Ref).Select(g => $"{(g.Count() == 1 ? "Timbre" : "Timbres")} {string.Join(",", g.Select(k => k.Timbre))} ({g.Key})"))} kept their slot numbers (no factory equivalent{(kronosProgramName != null ? ", or the source slot no longer holds the factory Program" : "")}) - convert and place those Programs at the same Nautilus slots.");
    }
}
