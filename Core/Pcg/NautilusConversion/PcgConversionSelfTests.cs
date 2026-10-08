namespace KronosScreenRemote;

// Off-hardware self-test for the Nautilus PCG conversion pieces built so far (RomWaveformMap,
// SampleZoneRomRemapper). Same convention as ObjectBodySelfTests/Librarian.SelfTest - pure,
// synchronous, returns failing check names (empty = all passed). Invoked from App.xaml.cs's
// --librarian-selftest. See the PCG converter v1 plan's Phase 3a/Phase 5.
static class PcgConversionSelfTests
{
    // Legacy KORG/MS-prefixed Bank UUID form (§7) - same 15-byte constant as
    // SampleReferenceWalker.KorgMsPrefix, duplicated here rather than exposed from that
    // display-only class, since a self-test fixture builder isn't a real caller of it.
    static readonly byte[] KorgMsPrefix =
        { 0x4B, 0x4F, 0x52, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0x4D, 0x53, 0x00 };

    // A real EXs pack's own Sxxx option-file text (verbatim content of F:\KRONOS EXs\
    // EXs377_KApro Film Atmos & Pads.zip's S377, read this session) - used to build a
    // synthetic ExsOptionIndex catalog that resolves a real-shaped raw UUID, so the
    // "known EXs product -> refuse" path is tested against real bytes, not an invented shape.
    const string Exs377OptionFileText = "EXs377\nKApro Film Atmos & Pads\n377\n2,uuid:e0ec1421-4c0f-49e8-9feb-83f74fc9fce8,EXs377 KApro Film Atmos & Pads";

    // Builds a minimal synthetic HD-1 Program body (WireSizeHd1 bytes) with oscillator 1's
    // zone 0 set to a legacy-form Sample zone referencing the given (nn, number) - same byte
    // layout LibRefs.IterProgramSampleZoneRefs reads (typeOff=2774, uuid at +1..+16, Number LE
    // at +18/+19), oscillator mode left at 0 (not Drums, so the zone is eligible).
    static byte[] BuildHd1BodyWithZoneRef(int nn, int number)
    {
        var body = new byte[ProgramFormatConverter.WireSizeHd1];
        const int typeOff = 2774;   // Osc1ZoneBase, zone 0
        body[typeOff] = 1;          // msType = 1 (Sample)
        Array.Copy(KorgMsPrefix, 0, body, typeOff + 1, 15);
        body[typeOff + 16] = (byte)nn;
        body[typeOff + 18] = (byte)(number & 0xFF);
        body[typeOff + 19] = (byte)((number >> 8) & 0xFF);
        return body;
    }

    // Same shape as BuildHd1BodyWithZoneRef but with a RAW (non-legacy-prefixed) 16-byte UUID
    // instead of the KORG/MS legacy form - for testing the RawUuid classification branch.
    static byte[] BuildHd1BodyWithRawUuidZoneRef(byte[] rawUuid, int number)
    {
        var body = new byte[ProgramFormatConverter.WireSizeHd1];
        const int typeOff = 2774;
        body[typeOff] = 1;
        Array.Copy(rawUuid, 0, body, typeOff + 1, 16);
        body[typeOff + 18] = (byte)(number & 0xFF);
        body[typeOff + 19] = (byte)((number >> 8) & 0xFF);
        return body;
    }

    // Builds a minimal synthetic Drum Kit body (real 38424-byte itemSize) with note 0, zone 0
    // set to an on, legacy-form sample reference - same layout DrumKitBody.IterSampleZoneRefs
    // reads (zoneBase=24, on/off at +0, uuid at +1..+16, SampleId LE at +18/+19).
    static byte[] BuildDrumKitBodyWithZoneRef(int nn, int sampleId)
    {
        var body = new byte[38424];
        const int zoneBase = 24;   // NoteBase, note 0, zone 0
        body[zoneBase] = 1;        // on
        Array.Copy(KorgMsPrefix, 0, body, zoneBase + 1, 15);
        body[zoneBase + 16] = (byte)nn;
        body[zoneBase + 18] = (byte)(sampleId & 0xFF);
        body[zoneBase + 19] = (byte)((sampleId >> 8) & 0xFF);
        return body;
    }

    // Builds a minimal synthetic Wave Sequence body (real 2216-byte itemSize) with step 0 set
    // to a Multisample-type, legacy-form sample reference - same layout WaveSequenceBody.
    // IterSampleStepRefs reads (stepBase=40, type at +0, uuid at +1..+16, Select LE at +18/+19).
    static byte[] BuildWaveSeqBodyWithStepRef(int nn, int select)
    {
        var body = new byte[2216];
        const int stepBase = 40;   // StepBase, step 0
        body[stepBase] = 0;        // type = Multisample
        Array.Copy(KorgMsPrefix, 0, body, stepBase + 1, 15);
        body[stepBase + 16] = (byte)nn;
        body[stepBase + 18] = (byte)(select & 0xFF);
        body[stepBase + 19] = (byte)((select >> 8) & 0xFF);
        return body;
    }

    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var noExsCatalog = ExsOptionIndex.FromCatalog("{}");

        // ── RomWaveformMap: loads a synthetic catalog, looks up by (section, index) ──
        var map = RomWaveformMap.FromCatalog(
            """[{"section":"ROM_MULTISAMPLES","kronos_index":9,"nautilus_index":20,"name":"A.Piano-M1"}]""");
        Check("rom-map-count", map.Count == 1);
        Check("rom-map-hit", map.TryMapRomIndex("ROM_MULTISAMPLES", 9, out int hitIdx) && hitIdx == 20);
        Check("rom-map-miss-unknown-index", !map.TryMapRomIndex("ROM_MULTISAMPLES", 1234, out _));
        Check("rom-map-miss-unknown-section", !map.TryMapRomIndex("ROM_DRUMSAMPLES", 9, out _));
        var audioMap = RomWaveformMap.FromCatalog(
            """[{"section":"ROM_MULTISAMPLES","kronos_index":9,"nautilus_index":20,"name":"x","audio":"differs"},{"section":"ROM_MULTISAMPLES","kronos_index":10,"nautilus_index":21,"name":"y","audio":"exact"}]""");
        Check("rom-map-audio-flag", !audioMap.IsAudioIdentical("ROM_MULTISAMPLES", 9) && audioMap.IsAudioIdentical("ROM_MULTISAMPLES", 10)
            && map.IsAudioIdentical("ROM_MULTISAMPLES", 9));
        var approxBody = BuildHd1BodyWithZoneRef(nn: 0, number: 9);
        Check("rom-map-approx-warning", ProgramConverter.KronosToNautilus(approxBody, audioMap, noExsCatalog,
            KarmaGeCatalog.FromCatalog("[]")).Warnings.Any(w => w.Contains("audio differs") && w.Contains("#9")));

        // ── SampleZoneRomRemapper: a mapped ROM (nn=0) zone reference gets remapped ──
        var hd1Mapped = BuildHd1BodyWithZoneRef(nn: 0, number: 9);
        var (remapped, unmapped1) = SampleZoneRomRemapper.RemapProgramHd1SampleZones(hd1Mapped, map, noExsCatalog);
        var (_, _, _, remappedNumber) = LibRefs.IterProgramSampleZoneRefs(remapped).First();
        Check("remap-rom-hit-changes-number", remappedNumber == 20);
        Check("remap-rom-hit-no-unmapped", unmapped1.Count == 0);
        var expectedTail = (byte[])hd1Mapped.Clone();
        expectedTail[2774 + 18] = remapped[2774 + 18];
        expectedTail[2774 + 19] = remapped[2774 + 19];
        Check("remap-preserves-every-other-byte", remapped.AsSpan().SequenceEqual(expectedTail));

        // ── An unmapped ROM index is a REFUSE candidate, not a silent skip ──
        var hd1Unmapped = BuildHd1BodyWithZoneRef(nn: 0, number: 9999);
        var (_, unmapped2) = SampleZoneRomRemapper.RemapProgramHd1SampleZones(hd1Unmapped, map, noExsCatalog);
        Check("remap-rom-miss-reports-unmapped", unmapped2.Count == 1 &&
            unmapped2[0].Reason.Contains("9999") && unmapped2[0].Reason.Contains("ROM_MULTISAMPLES"));

        // ── Sampling Mode (RAM, nn=2 -> legacy bank 1) passes through unchanged - not
        // platform content, never remapped, never reported as unmapped ──
        var hd1Ram = BuildHd1BodyWithZoneRef(nn: 2, number: 42);
        var (ramResult, unmapped3) = SampleZoneRomRemapper.RemapProgramHd1SampleZones(hd1Ram, map, noExsCatalog);
        Check("remap-ram-passthrough", ramResult.AsSpan().SequenceEqual(hd1Ram) && unmapped3.Count == 0);

        // ── Legacy-form EXs (nn=4 -> legacy bank 2 -> EXs1) ALWAYS refuses - there is no
        // lookup table for EXs content at all (see SampleZoneRomRemapper's header comment:
        // cross-platform availability of a purchased pack is unverified, not merely unmapped) ──
        var hd1Exs = BuildHd1BodyWithZoneRef(nn: 4, number: 5);
        var (_, unmapped4) = SampleZoneRomRemapper.RemapProgramHd1SampleZones(hd1Exs, map, noExsCatalog);
        Check("remap-legacy-exs-always-refuses", unmapped4.Count == 1 && unmapped4[0].Reason.Contains("EXs1"));

        // ── Raw UUID resolving to a KNOWN EXs product (via ExsOptionIndex) also refuses -
        // confirmed EXs bank identity (small int <=126 OR real UUID >=127, per F:\KRONOS EXs\'s
        // real Sxxx option files read this session) doesn't establish cross-platform byte
        // compatibility, so a resolvable EXs product still refuses like any other EXs ref ──
        var knownExsCatalog = ExsOptionIndex.FromCatalog($$"""{"377": {{System.Text.Json.JsonSerializer.Serialize(Exs377OptionFileText)}} }""");
        var rawUuidBytes = ExsOptionIndex.RawUuidBytes("e0ec1421-4c0f-49e8-9feb-83f74fc9fce8")!;
        var hd1KnownExsUuid = BuildHd1BodyWithRawUuidZoneRef(rawUuidBytes, number: 3);
        var (_, unmapped5) = SampleZoneRomRemapper.RemapProgramHd1SampleZones(hd1KnownExsUuid, map, knownExsCatalog);
        Check("remap-raw-uuid-known-exs-product-refuses", unmapped5.Count == 1 &&
            unmapped5[0].Reason.Contains("KApro Film Atmos & Pads"));

        // ── Raw UUID that DOESN'T resolve against the catalog is genuine user/3rd-party
        // content - passes through unchanged, not refused (this is the fix for a real gap:
        // these used to fall through silently with no Unmapped entry at all AND no test
        // covering the case) ──
        var unknownRawUuid = new byte[16];
        for (int i = 0; i < 16; i++) unknownRawUuid[i] = (byte)(i * 7 + 1);   // arbitrary non-legacy, non-zero bytes
        var hd1UnknownRawUuid = BuildHd1BodyWithRawUuidZoneRef(unknownRawUuid, number: 3);
        var (userResult, unmapped6) = SampleZoneRomRemapper.RemapProgramHd1SampleZones(hd1UnknownRawUuid, map, noExsCatalog);
        Check("remap-raw-uuid-unresolved-passthrough", userResult.AsSpan().SequenceEqual(hd1UnknownRawUuid) && unmapped6.Count == 0);

        // ── Drum Kit zone remap: same shape, ROM_DRUMSAMPLES section ──
        var dkMap = RomWaveformMap.FromCatalog(
            """[{"section":"ROM_DRUMSAMPLES","kronos_index":5,"nautilus_index":99,"name":"BD-Test"}]""");
        var dkMapped = BuildDrumKitBodyWithZoneRef(nn: 0, sampleId: 5);
        var (dkRemapped, dkUnmapped1) = SampleZoneRomRemapper.RemapDrumKitSampleZones(dkMapped, dkMap, noExsCatalog);
        var (_, _, _, dkRemappedId) = DrumKitBody.IterSampleZoneRefs(dkRemapped).First();
        Check("drumkit-remap-rom-hit-changes-id", dkRemappedId == 99);
        Check("drumkit-remap-rom-hit-no-unmapped", dkUnmapped1.Count == 0);

        var dkUnmappedBody = BuildDrumKitBodyWithZoneRef(nn: 0, sampleId: 9999);
        var (_, dkUnmapped2) = SampleZoneRomRemapper.RemapDrumKitSampleZones(dkUnmappedBody, dkMap, noExsCatalog);
        Check("drumkit-remap-rom-miss-reports-unmapped", dkUnmapped2.Count == 1 &&
            dkUnmapped2[0].Reason.Contains("9999") && dkUnmapped2[0].Reason.Contains("ROM_DRUMSAMPLES"));

        // ── Wave Sequence step remap: same shape, ROM_MULTISAMPLES section ──
        var wsMapped = BuildWaveSeqBodyWithStepRef(nn: 0, select: 9);
        var (wsRemapped, wsUnmapped1) = SampleZoneRomRemapper.RemapWaveSequenceSampleSteps(wsMapped, map, noExsCatalog);
        var (_, _, wsRemappedSelect) = WaveSequenceBody.IterSampleStepRefs(wsRemapped).First();
        Check("waveseq-remap-rom-hit-changes-select", wsRemappedSelect == 20);
        Check("waveseq-remap-rom-hit-no-unmapped", wsUnmapped1.Count == 0);

        var wsUnmappedBody = BuildWaveSeqBodyWithStepRef(nn: 0, select: 9999);
        var (_, wsUnmapped2) = SampleZoneRomRemapper.RemapWaveSequenceSampleSteps(wsUnmappedBody, map, noExsCatalog);
        Check("waveseq-remap-rom-miss-reports-unmapped", wsUnmapped2.Count == 1 &&
            wsUnmapped2[0].Reason.Contains("9999") && wsUnmapped2[0].Reason.Contains("ROM_MULTISAMPLES"));

        // ── KarmaFieldStripper: zeroes [1304,2557], preserves everything outside it ──
        var karmaBody = new byte[ProgramFormatConverter.WireSizeHd1];
        for (int i = 0; i < karmaBody.Length; i++) karmaBody[i] = (byte)((i * 17 + 3) & 0xFF);   // non-zero everywhere
        var (stripped, wasStripped) = KarmaFieldStripper.StripKarmaFields(karmaBody);
        Check("karma-strip-reports-stripped", wasStripped);
        bool rangeAllZero = true;
        for (int i = KarmaFieldStripper.KarmaStart; i <= KarmaFieldStripper.KarmaEnd; i++)
            if (stripped[i] != 0) rangeAllZero = false;
        Check("karma-strip-zeroes-whole-range", rangeAllZero);
        Check("karma-strip-preserves-byte-before-range", stripped[KarmaFieldStripper.KarmaStart - 1] == karmaBody[KarmaFieldStripper.KarmaStart - 1]);
        Check("karma-strip-preserves-byte-after-range", stripped[KarmaFieldStripper.KarmaEnd + 1] == karmaBody[KarmaFieldStripper.KarmaEnd + 1]);
        Check("karma-strip-preserves-name-field", stripped[0] == karmaBody[0] && stripped[10] == karmaBody[10]);

        var alreadyBlankKarma = new byte[ProgramFormatConverter.WireSizeHd1];   // all zero - INIT-like
        var (_, wasStrippedBlank) = KarmaFieldStripper.StripKarmaFields(alreadyBlankKarma);
        Check("karma-strip-noop-on-already-blank", !wasStrippedBlank);

        // ── KarmaFieldStripper: bit-level Chord Mode/Source strip at 2558/2562, preserving the
        // other fields sharing those same bytes (Oscillator Mode/Favorite/Hold/Assign Mode at
        // 2558; Unison Thickness at 2562) ──
        var chordBody = new byte[ProgramFormatConverter.WireSizeHd1];
        chordBody[2558] = 0b1111_1111;   // Oscillator Mode=7, Chord Mode=3, Favorite/Hold/Assign Mode all set
        chordBody[2562] = 0b1111_1111;   // Unison Thickness=15, Chord Source=7
        var (chordStripped, chordWasStripped) = KarmaFieldStripper.StripKarmaFields(chordBody);
        Check("karma-strip-reports-stripped-for-chord-only", chordWasStripped);
        Check("karma-strip-clears-chord-mode-bits", (chordStripped[2558] & 0b0001_1000) == 0);
        Check("karma-strip-preserves-osc-mode-and-other-2558-bits", (chordStripped[2558] & 0b1110_0111) == 0b1110_0111);
        Check("karma-strip-clears-chord-source-bits", (chordStripped[2562] & 0b0111_0000) == 0);
        Check("karma-strip-preserves-unison-thickness", (chordStripped[2562] & 0b0000_1111) == 0b0000_1111);

        var noChordBody = new byte[ProgramFormatConverter.WireSizeHd1];
        noChordBody[2558] = 0b1110_0111;   // same non-KARMA bits set, Chord Mode bits already 0
        noChordBody[2562] = 0b0000_1111;   // same non-KARMA bits set, Chord Source bits already 0
        var (_, noChordWasStripped) = KarmaFieldStripper.StripKarmaFields(noChordBody);
        Check("karma-strip-no-chord-bits-set-not-reported-stripped", !noChordWasStripped);

        // ── KarmaGeCatalog + KarmaFieldStripper.DescribeGeAssignment: names a real GE ──
        var geCatalog = KarmaGeCatalog.FromCatalog(
            """[{"ge_number":47,"name":"Comp/Lead Generated-Riff"}]""");
        Check("ge-catalog-count", geCatalog.Count == 1);
        Check("ge-catalog-hit", geCatalog.NameForGeNumber(47) == "Comp/Lead Generated-Riff");
        Check("ge-catalog-miss", geCatalog.NameForGeNumber(999) == null);

        var bodyWithGe = new byte[ProgramFormatConverter.WireSizeHd1];
        bodyWithGe[1814] = 47;   // GE Number = 47, LE
        var geWarning = KarmaFieldStripper.DescribeGeAssignment(bodyWithGe, geCatalog);
        Check("ge-warning-names-known-ge", geWarning != null && geWarning.Contains("#47") && geWarning.Contains("Comp/Lead Generated-Riff"));

        var bodyWithUnknownGe = new byte[ProgramFormatConverter.WireSizeHd1];
        bodyWithUnknownGe[1814] = 200;
        var unknownGeWarning = KarmaFieldStripper.DescribeGeAssignment(bodyWithUnknownGe, geCatalog);
        Check("ge-warning-unknown-ge-still-names-number", unknownGeWarning != null &&
            unknownGeWarning.Contains("#200") && unknownGeWarning.Contains("name unknown"));

        var bodyWithGeZero = new byte[ProgramFormatConverter.WireSizeHd1];   // GE Number 0 - no warning
        Check("ge-warning-none-for-ge-zero", KarmaFieldStripper.DescribeGeAssignment(bodyWithGeZero, geCatalog) == null);

        // ── ProgramConverter: full pipeline, success case ──
        // A ROM-mapped zone + a GE assignment + non-zero KARMA bytes elsewhere in range, all
        // in one body - exercises all 3 sub-pieces together, in one pass.
        var fullBody = BuildHd1BodyWithZoneRef(nn: 0, number: 9);   // maps 9 -> 20 via `map`
        fullBody[1814] = 47;                                       // GE Number = 47
        fullBody[1400] = 0xAB;                                     // arbitrary non-zero KARMA byte
        var success = ProgramConverter.KronosToNautilus(fullBody, map, noExsCatalog, geCatalog);
        Check("converter-success-body-not-null", success.NautilusBody != null);
        Check("converter-success-no-refusals", success.Refusals.Count == 0);
        Check("converter-success-ge-warning-present", success.Warnings.Any(w => w.Contains("#47") && w.Contains("Comp/Lead Generated-Riff")));
        Check("converter-success-karma-stripped-warning-present", success.Warnings.Any(w => w.Contains("KARMA performance data")));
        if (success.NautilusBody != null)
        {
            // LibRefs reads Kronos offsets; the Nautilus body carries the same zones
            // NautilusSizeDelta bytes earlier, so re-pad it to Kronos layout to inspect them.
            var asKronos = new byte[fullBody.Length];
            Array.Copy(success.NautilusBody, 0, asKronos, 0, ProgramConverter.NautilusTailStart);
            Array.Copy(success.NautilusBody, ProgramConverter.NautilusTailStart, asKronos, ProgramConverter.KronosTailStart,
                success.NautilusBody.Length - ProgramConverter.NautilusTailStart);
            var (_, _, _, convertedNumber) = LibRefs.IterProgramSampleZoneRefs(asKronos).First();
            Check("converter-success-zone-remapped", convertedNumber == 20);
            Check("converter-success-karma-zeroed", success.NautilusBody[1400] == 0);
            Check("converter-success-nautilus-size", success.NautilusBody.Length == ProgramFormatConverter.WireSizeHd1 - 6);
            Check("converter-success-no-template-warning", success.Warnings.Any(w => w.Contains("zero-filled")));
        }

        // ── ProgramConverter: Kronos -> Nautilus relayout. Head [0,1280) copied, middle taken
        // from the Nautilus template, tail from Kronos 2558 lands at Nautilus 2552. ──
        var layoutSrc = new byte[ProgramFormatConverter.WireSizeExi];
        for (int i = 0; i < layoutSrc.Length; i++) layoutSrc[i] = (byte)(i * 7 + 3);
        var layoutTemplate = new byte[ProgramFormatConverter.WireSizeExi - 6];
        Array.Fill(layoutTemplate, (byte)0x5A);
        var layout = ProgramConverter.KronosToNautilus(layoutSrc, map, noExsCatalog, geCatalog, layoutTemplate);
        var lb = layout.NautilusBody;
        Check("relayout-exi-size", lb != null && lb.Length == 4954);
        Check("relayout-head-copied", lb != null && lb[0] == layoutSrc[0] && lb[1279] == layoutSrc[1279]);
        Check("relayout-middle-from-template", lb != null && lb[1280] == 0x5A && lb[2551] == 0x5A);
        Check("relayout-tail-shifted", lb != null && lb[2553] == layoutSrc[2559] && lb[4953] == layoutSrc[4959]);
        Check("relayout-no-template-warning-when-supplied", !layout.Warnings.Any(w => w.Contains("zero-filled")));
        var badTemplate = ProgramConverter.KronosToNautilus(layoutSrc, map, noExsCatalog, geCatalog, new byte[3700]);
        Check("relayout-wrong-template-size-refused", badTemplate.NautilusBody == null && badTemplate.Refusals.Count == 1);

        // ── ProgramConverter: "Basic Arp" GE -> Nautilus preset, written only to the chosen Scenes ──
        static int ScenePn(byte[] b, int n) => b[ArpConversion.SceneBase + n * ArpConversion.SceneStride]
            | (b[ArpConversion.SceneBase + n * ArpConversion.SceneStride + 1] << 8);
        var arpSrc = new byte[ProgramFormatConverter.WireSizeHd1];
        arpSrc[1814] = 25;                                         // Basic Arp RANDOM -> P4
        var arpTemplate = new byte[ProgramFormatConverter.WireSizeHd1 - 6];
        Array.Fill(arpTemplate, (byte)0x5A);
        var arpDefault = ProgramConverter.KronosToNautilus(arpSrc, map, noExsCatalog, geCatalog, arpTemplate).NautilusBody!;
        Check("arp-default-scene1-preset", ScenePn(arpDefault, 0) == 4);
        Check("arp-default-scenes2-4-untouched", ScenePn(arpDefault, 1) == 0x5A5A && ScenePn(arpDefault, 3) == 0x5A5A);
        var arpAll = ProgramConverter.KronosToNautilus(arpSrc, map, noExsCatalog, geCatalog, arpTemplate,
            new ArpConversionOptions { ProgramScenes = 0b1111 }).NautilusBody!;
        Check("arp-all-scenes-preset", Enumerable.Range(0, 4).All(n => ScenePn(arpAll, n) == 4));
        var arpNone = ProgramConverter.KronosToNautilus(arpSrc, map, noExsCatalog, geCatalog, arpTemplate,
            new ArpConversionOptions { ProgramScenes = 0 });
        Check("arp-no-scenes-untouched", ScenePn(arpNone.NautilusBody!, 0) == 0x5A5A);
        Check("arp-mapped-ge-warning", ProgramConverter.KronosToNautilus(arpSrc, map, noExsCatalog, geCatalog, arpTemplate)
            .Warnings.Any(w => w.Contains("#25") && w.Contains("P4") && w.Contains("Scene 1")));
        Check("arp-ge-map", ArpConversion.PresetForGe(26) == 0 && ArpConversion.PresetForGe(24) == 1 &&
            ArpConversion.PresetForGe(22) == 2 && ArpConversion.PresetForGe(23) == 3 && ArpConversion.PresetForGe(47) == null);
        Check("arp-unmapped-ge-scene-untouched", ScenePn(success.NautilusBody ?? new byte[3700], 0) == 0);
        // ── KarmaGePatternLibrary + ProgramConverter: transcribed GE patterns ──
        {
            var pat = new byte[ArpPatternTranscriber.PatternSize]; pat[0] = (byte)'T'; pat[25] = 8;
            var lib = KarmaGePatternLibrary.Parse(KarmaGePatternLibrary.Pack(new[]
            {
                new KarmaGePatternLibrary.Entry(47, ArpPatternTranscriber.Encoding.Fixed, 3, 0.95, pat),
                new KarmaGePatternLibrary.Entry(48, ArpPatternTranscriber.Encoding.Normal, 4, 0.50, pat),
            }))!;
            Check("ge-library-roundtrip", lib.Count == 2 && lib.ForGe(47)!.Mode == ArpPatternTranscriber.Encoding.Fixed
                && lib.ForGe(47)!.StepsPerQuarter == 3 && Math.Abs(lib.ForGe(47)!.Score - 0.95) < 1e-9 && lib.ForGe(47)!.Pattern[0] == 'T');
            Check("ge-library-rejects-garbage", KarmaGePatternLibrary.Parse(new byte[10]) == null);
            var embedded = KarmaGePatternLibrary.FromCatalog();
            Check("ge-library-embedded-loads", embedded.Count > 0 && embedded.ForGe(26) is { } up
                && up.Pattern.Length == ArpPatternTranscriber.PatternSize && up.Score > 0.9);

            var src = new byte[ProgramFormatConverter.WireSizeHd1]; src[1814] = 47;
            var withSlot = ProgramConverter.KronosToNautilus(src, map, noExsCatalog, geCatalog, arpTemplate,
                new ArpConversionOptions { TranscribedPatternSlot = 9 }, lib);
            var nb = withSlot.NautilusBody!;
            Check("ge-transcribed-scene1", ScenePn(nb, 0) == 2053 + 9 && ScenePn(nb, 1) == 0x5A5A);
            Check("ge-transcribed-scene-settings", ((nb[2442] >> 2) & 7) == 4 && nb[2444] == 100 && nb[2445] == 0x81);
            Check("ge-transcribed-pattern-returned", withSlot.ArpPatternToStore is { Slot: 9 } a && a.Pattern[0] == 'T');
            var noSlot = ProgramConverter.KronosToNautilus(src, map, noExsCatalog, geCatalog, arpTemplate, null, lib);
            Check("ge-transcribed-needs-slot", noSlot.ArpPatternToStore == null && ScenePn(noSlot.NautilusBody!, 0) == 0x5A5A
                && noSlot.Warnings.Any(w => w.Contains("no bank-B slot")));
            src[1814] = 48;
            var lowScore = ProgramConverter.KronosToNautilus(src, map, noExsCatalog, geCatalog, arpTemplate,
                new ArpConversionOptions { TranscribedPatternSlot = 9 }, lib);
            Check("ge-transcribed-score-threshold", lowScore.ArpPatternToStore == null && ScenePn(lowScore.NautilusBody!, 0) == 0x5A5A);
        }
        // ── ProgramConverter: KARMA Common tempo / latch / switch carry into Scene Common ──
        {
            var src = new byte[ProgramFormatConverter.WireSizeHd1];
            src[1814] = 26;                              // Basic Arp UP -> P0, so the Arp switch follows KARMA
            src[1304] = 0x70; src[1305] = 0x2B;          // 111.20 BPM
            src[1306] = 0b1100_0000;                     // KARMA on, latch on
            var tmpl = new byte[ProgramFormatConverter.WireSizeHd1 - 6];
            var nb = ProgramConverter.KronosToNautilus(src, map, noExsCatalog, geCatalog, tmpl).NautilusBody!;
            Check("program-karma-common-carried", nb[1350] == 0x70 && nb[1351] == 0x2B && (nb[1349] & 0b11) == 0b11);
            src[1814] = 47;                              // untranslatable GE: switch stays as the template had it
            var nb2 = ProgramConverter.KronosToNautilus(src, map, noExsCatalog, geCatalog, tmpl).NautilusBody!;
            Check("program-arp-switch-only-when-assigned", (nb2[1349] & 0b10) == 0 && (nb2[1349] & 1) == 1);
        }

        // ── CombiConverter ──
        {
            var kc = new byte[CombiConverter.KronosSize];
            for (int i = 0; i < kc.Length; i++) kc[i] = (byte)(i * 13 + 5);
            // KARMA Modules: A = GE 26 (P0), B = GE 24 (P1), C = GE 47 (dropped), D = 0 (unused).
            void Module(int m, int ge, byte inCh, byte outCh, byte top, byte bottom)
            {
                int k = 1814 + 744 * m; kc[k] = (byte)ge; kc[k + 1] = (byte)(ge >> 8); kc[k + 2] = inCh; kc[k + 3] = outCh; kc[k + 5] = top; kc[k + 6] = bottom;
            }
            Module(0, 26, 0x10, 3, 100, 20); Module(1, 24, 0x80 | 2, 5, 90, 30); Module(2, 47, 0, 0, 0, 0); Module(3, 0, 0, 0, 0, 0);
            kc[1304] = 0xE0; kc[1305] = 0x2E; kc[1306] = 0b1000_0000;   // 120 BPM, KARMA on, latch off
            kc[4832] = 0xFF; kc[4833] = 0xFF; kc[4835] = 0xFF; kc[4837] = 0xFF; kc[4796] = 0xFF; kc[4798] = 0x42;
            var ct = new byte[CombiConverter.NautilusSize]; Array.Fill(ct, (byte)0x5A);
            var cr = CombiConverter.KronosToNautilus(kc, geCatalog, ct);
            var cb = cr.NautilusBody!;
            Check("combi-size-and-layout", cb.Length == 5640 && cb[0] == kc[0] && cb[1267] == kc[1267]
                && cb[1268] == 0x5A && cb[2620] == kc[4790] && cb[5639] == kc[7809]);
            Check("combi-timbre-program-ref-shifted", cb[2632] == kc[4802] && cb[2633] == kc[4803] && cb[2632 + 188] == kc[4802 + 188]);
            int ScenePnC(int n, int arpOff) => cb[2440 + 44 * n + arpOff] | cb[2440 + 44 * n + arpOff + 1] << 8;
            Check("combi-arp-a-b-scene1", ScenePnC(0, 0) == 0 && ScenePnC(0, 16) == 1 && ScenePnC(1, 0) == 0x5A5A);
            Check("combi-arp-channels-zones", cb[2440 + 12] == 3 && cb[2440 + 13] == 0x10 && cb[2440 + 16 + 12] == 5 && cb[2440 + 16 + 13] == 2
                && cb[2612] == 100 && cb[2613] == 20 && cb[2616] == 90 && cb[2617] == 30);
            Check("combi-karma-common", cb[1350] == 0xE0 && cb[1351] == 0x2E && (cb[1349] & 0b11) == 0b10);
            Check("combi-kronos-only-bits-cleared", cb[2662] == (kc[4832] & 0x87) && cb[2663] == (kc[4833] & 0x9F)
                && cb[2665] == kc[4835] && cb[2667] == (kc[4837] & 0x7F) && cb[2626] == 0x5A && cb[2631] == 0x5A);
            Check("combi-module-c-dropped-warning", cr.Warnings.Any(w => w.Contains("Module C") && w.Contains("#47") && w.Contains("dropped")));
            var cc = CombiConverter.KronosToNautilus(kc, geCatalog, ct, new ArpConversionOptions { CombiArpASource = 2, CombiArpBSource = null });
            Check("combi-module-choice", cc.Warnings.Any(w => w.Contains("Module A") && w.Contains("dropped"))
                && cc.Warnings.Any(w => w.Contains("Module C") && w.Contains("no translation")));
            // Timbre Program references: factory equivalents by default, verified against the source's own names.
            var refMap = ProgramRefMap.FromJson("""[{"kronos_bank":0,"kronos_index":5,"kronos_name":"Grand","nautilus_bank":17,"nautilus_index":9,"nautilus_name":"Grand N","source":"korg"}]""");
            var kr = (byte[])kc.Clone();
            for (int t = 0; t < 16; t++) { int o = 4802 + 188 * t; kr[o] = 5; kr[o + 1] = 0; kr[o + 2] = (byte)(t < 2 ? 0x20 : 0); }   // timbres 1-2 active
            var rm = CombiConverter.KronosToNautilus(kr, geCatalog, ct, null, null, TimbreProgramRefs.FactoryEquivalents, refMap).NautilusBody!;
            Check("combi-ref-remapped", rm[2632] == 9 && rm[2633] == 17 && rm[2632 + 188] == 9 && rm[2632 + 2 * 188] == 5);
            var keep = CombiConverter.KronosToNautilus(kr, geCatalog, ct, null, null, TimbreProgramRefs.KeepSlots, refMap).NautilusBody!;
            Check("combi-ref-keep-slots", keep[2632] == 5 && keep[2633] == 0);
            var edited = CombiConverter.KronosToNautilus(kr, geCatalog, ct, null, null, TimbreProgramRefs.FactoryEquivalents, refMap, (b, i) => "My Edit");
            Check("combi-ref-not-remapped-when-source-edited", edited.NautilusBody![2632] == 5 && edited.Warnings.Any(w => w.Contains("My Edit")));
            Check("combi-wrong-size-refused", CombiConverter.KronosToNautilus(new byte[100], geCatalog).NautilusBody == null);
        }

        Check("arp-combi-default-drops-cd", ArpConversionOptions.Default.CombiArpASource == 0 && ArpConversionOptions.Default.CombiArpBSource == 1);

        // ── ArpPatternTranscriber: synthetic KARMA captures -> Nautilus Arp Pattern ──
        {
            int[] upSeq = { 60, 64, 67, 72, 76, 79 };   // Basic Arp UP over 2 octaves, C-E-G held
            var rnd = new Random(1);
            var up = new List<ArpPatternTranscriber.Note> { new(0.004, 60, 99, 0.01) };   // retrigger, as captured
            for (int i = 0; i < 24; i++)
                up.Add(new(0.02 + i * 0.125 + rnd.NextDouble() * 0.01, upSeq[i % 6], 99, 0.125));
            var r = ArpPatternTranscriber.Transcribe(up, new[] { 67, 60, 64 }, 120, "Test Up")!;
            int Tones(byte[] p, int s) => p[40 + s * 8 + 4] | p[40 + s * 8 + 5] << 8;
            Check("transcribe-up-length", r.Length == 6 && r.Pattern[25] == 6);
            Check("transcribe-up-grid-16th", r.StepsPerQuarter == 4);
            Check("transcribe-up-tones", Tones(r.Pattern, 0) == 1 && Tones(r.Pattern, 1) == 2 && Tones(r.Pattern, 2) == 4 && Tones(r.Pattern, 3) == 1);
            Check("transcribe-up-octave-offset", r.Pattern[40 + 3 * 8] == 12 && r.Pattern[40] == 0);
            Check("transcribe-up-legato-velocity", r.Pattern[41] == 101 && r.Pattern[42] == 99 && r.Lossy.Count == 0);
            Check("transcribe-size-name", r.Pattern.Length == 556 && System.Text.Encoding.ASCII.GetString(r.Pattern, 0, 7) == "Test Up");

            var trip = new List<ArpPatternTranscriber.Note>();   // 8th-note triplets, alternating C / chord
            for (int i = 0; i < 18; i++)
            {
                double t = i * (0.5 / 3);
                if (i % 2 == 0) trip.Add(new(t, 60, 80, 0.08));
                else { trip.Add(new(t, 64, 70, 0.08)); trip.Add(new(t + 0.004, 67, 70, 0.08)); }
            }
            var rt = ArpPatternTranscriber.Transcribe(trip, new[] { 60, 64, 67 }, 120, "Trip")!;
            Check("transcribe-triplet-grid", rt.StepsPerQuarter == 3 && rt.Length == 2);
            Check("transcribe-chord-step", Tones(rt.Pattern, 1) == 0b110 && rt.Pattern[40 + 8 + 2] == 70);
            Check("transcribe-rest-steps-blank", Tones(rt.Pattern, 5) == 0 && rt.Pattern[40 + 5 * 8 + 1] == 50);

            Check("transcribe-up-normal-exact", r.Mode == ArpPatternTranscriber.Encoding.Normal && r.Score == 1.0);

            var wide = new List<ArpPatternTranscriber.Note>();   // C4+C5 together, then E4: needs two offsets on step 1
            for (int i = 0; i < 16; i++)
            {
                double t = i * 0.125;
                if (i % 2 == 0) { wide.Add(new(t, 60, 90, 0.1)); wide.Add(new(t + 0.003, 72, 90, 0.1)); }
                else wide.Add(new(t, 64, 90, 0.1));
            }
            var rw = ArpPatternTranscriber.Transcribe(wide, new[] { 60, 64, 67 }, 120, "Wide")!;
            Check("transcribe-wide-picks-fixed", rw.Mode == ArpPatternTranscriber.Encoding.Fixed && rw.Score == 1.0 && rw.NormalScore < 1.0);
            Check("transcribe-wide-fixed-tones", rw.Pattern[24] == 0x30 && rw.Pattern[28] == 60 && rw.Pattern[29] == 64 && rw.Pattern[30] == 72);
            Check("simulate-fixed-alltones", ArpPatternTranscriber.SimulateStep(rw.Pattern, new[] { 62 }, 0).SetEquals(new[] { 60, 72 }));
            var asPlayed = (byte[])rw.Pattern.Clone(); asPlayed[24] = 0x10;
            Check("simulate-fixed-asplayed-needs-keys", ArpPatternTranscriber.SimulateStep(asPlayed, new[] { 62 }, 0).SetEquals(new[] { 60 }));

            var midi = new List<(double, byte[])> { (0.0, new byte[] { 0x90, 60, 90 }), (0.1, new byte[] { 0x80, 60, 0 }),
                (0.1, new byte[] { 0x90, 62, 80 }), (0.2, new byte[] { 0x90, 62, 0 }) };
            var paired = ArpPatternTranscriber.NotesFromMidi(midi);
            Check("transcribe-midi-pairing", paired.Count == 2 && paired[1].Pitch == 62 && Math.Abs(paired[1].DurSec - 0.1) < 1e-9);
        }

        // ── ProgramConverter: refusal case - unmapped ROM zone stops the whole conversion ──
        var refuseBody = BuildHd1BodyWithZoneRef(nn: 0, number: 9999);
        var refused = ProgramConverter.KronosToNautilus(refuseBody, map, noExsCatalog, geCatalog);
        Check("converter-refusal-body-null", refused.NautilusBody == null);
        Check("converter-refusal-has-refusal", refused.Refusals.Count == 1 && refused.Refusals[0].Contains("9999"));

        // ── NautilusPcgWriter: round-trip a converted Program through a real file and read it
        // back with NautilusPcgFile/NautilusPcgObjectExtractor (Phase 2) - the strongest
        // available self-check without real hardware: an independently-written reader must
        // recognize the file, find exactly the one Program, with byte-identical content and no
        // checksum/rejection complaints. ──
        var namedBody = ProgramBody.WriteName(success.NautilusBody!, "ROUNDTRIP TEST");
        var pcgFile = NautilusPcgWriter.WriteSingleProgram(namedBody, isExi: false);
        Check("writer-file-starts-with-korg-and-nautilus-model", pcgFile.Length > 16 &&
            System.Text.Encoding.ASCII.GetString(pcgFile, 0, 4) == "KORG" && pcgFile[4] == 0xDD);

        var roundTripped = NautilusPcgFile.Open(pcgFile);
        Check("writer-roundtrip-file-recognized", roundTripped != null);
        if (roundTripped != null)
        {
            Check("writer-roundtrip-no-rejected-banks", roundTripped.RejectedBanks.Count == 0);
            Check("writer-roundtrip-no-checksum-warnings", roundTripped.ChecksumWarnings.Count == 0);
            Check("writer-roundtrip-exactly-one-program", roundTripped.Objects.Count(o => o.ObjType == LibObj.Program) == 1);
            var readBack = roundTripped.Objects.FirstOrDefault(o => o.ObjType == LibObj.Program);
            Check("writer-roundtrip-body-byte-identical", readBack != null && readBack.Body.AsSpan().SequenceEqual(namedBody));
            Check("writer-roundtrip-name-readable", readBack != null && readBack.Name == "ROUNDTRIP TEST");
        }

        // ── NautilusPcgWriter: same round-trip for Drum Kit and Wave Sequence ──
        var namedDkBody = DrumKitBody.WriteName(dkRemapped, "ROUNDTRIP DK");
        var dkPcgFile = NautilusPcgWriter.WriteSingleDrumKit(namedDkBody);
        var dkRoundTripped = NautilusPcgFile.Open(dkPcgFile);
        Check("writer-roundtrip-drumkit-recognized", dkRoundTripped != null &&
            dkRoundTripped.RejectedBanks.Count == 0 && dkRoundTripped.ChecksumWarnings.Count == 0);
        var dkReadBack = dkRoundTripped?.Objects.FirstOrDefault(o => o.ObjType == LibObj.DrumKit);
        Check("writer-roundtrip-drumkit-body-and-name", dkReadBack != null &&
            dkReadBack.Body.AsSpan().SequenceEqual(namedDkBody) && dkReadBack.Name == "ROUNDTRIP DK");

        var namedWsBody = WaveSequenceBody.WriteName(wsRemapped, "ROUNDTRIP WS");
        var wsPcgFile = NautilusPcgWriter.WriteSingleWaveSequence(namedWsBody);
        var wsRoundTripped = NautilusPcgFile.Open(wsPcgFile);
        Check("writer-roundtrip-waveseq-recognized", wsRoundTripped != null &&
            wsRoundTripped.RejectedBanks.Count == 0 && wsRoundTripped.ChecksumWarnings.Count == 0);
        var wsReadBack = wsRoundTripped?.Objects.FirstOrDefault(o => o.ObjType == LibObj.WaveSequence);
        Check("writer-roundtrip-waveseq-body-and-name", wsReadBack != null &&
            wsReadBack.Body.AsSpan().SequenceEqual(namedWsBody) && wsReadBack.Name == "ROUNDTRIP WS");

        return fails;
    }
}
