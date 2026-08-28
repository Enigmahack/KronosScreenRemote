namespace KronosScreenRemote;

// Off-hardware self-test for SampleReferenceWalker - same convention as
// Librarian.SelfTest/ObjectBodySelfTests.SelfTest (pure, synchronous, returns failing check
// names). Invoked from App.xaml.cs's --librarian-selftest.
//
// Byte offsets used to build synthetic bodies below mirror LibRefs/DrumKitBody/
// WaveSequenceBody's own private layout constants (2774/3240/22/18 for HD-1 Program zones;
// 24/300/34 for Drum Kit zones; 40/34 for Wave Sequence steps) - see
// scratch_debug/pcg-sample-dependency-investigation.md for the hardware evidence behind them.
static class SampleReferenceWalkerSelfTests
{
    // Builds a legacy-form 16-byte Bank UUID: KORG_MS_PREFIX + nn, where
    // nn = (legacyBankNumber << 1) | stereoFlag (§7's own formula).
    static byte[] LegacyUuid(int legacyBankNumber, int stereoFlag = 0)
    {
        var uuid = new byte[16];
        byte[] prefix = { 0x4B, 0x4F, 0x52, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0x4D, 0x53, 0x00 };
        prefix.CopyTo(uuid, 0);
        uuid[15] = (byte)((legacyBankNumber << 1) | stereoFlag);
        return uuid;
    }

    static void WriteHd1Zone(byte[] body, int osc, int zone, int msType, byte[]? uuid, int number)
    {
        int oscBase = osc == 0 ? 2774 : 3240;
        int typeOff = oscBase + zone * 22;
        body[typeOff] = (byte)msType;
        if (uuid != null) uuid.CopyTo(body, typeOff + 1);
        body[typeOff + 18] = (byte)(number & 0xFF);
        body[typeOff + 19] = (byte)((number >> 8) & 0xFF);
    }

    static void WriteDrumKitZone(byte[] body, int note, int zone, bool on, byte[]? uuid, int sampleId)
    {
        int zoneBase = 24 + note * 300 + zone * 34;
        body[zoneBase] = on ? (byte)1 : (byte)0;
        if (uuid != null) uuid.CopyTo(body, zoneBase + 1);
        body[zoneBase + 18] = (byte)(sampleId & 0xFF);
        body[zoneBase + 19] = (byte)((sampleId >> 8) & 0xFF);
    }

    static void WriteWaveSeqStep(byte[] body, int step, int type, byte[]? uuid, int select)
    {
        int stepBase = 40 + step * 34;
        body[stepBase] = (byte)type;
        if (uuid != null) uuid.CopyTo(body, stepBase + 1);
        body[stepBase + 18] = (byte)(select & 0xFF);
        body[stepBase + 19] = (byte)((select >> 8) & 0xFF);
    }

    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── HD-1 Program: real Sample-mode zone -> EXs, ROM skipped, RAM labeled ──
        {
            var body = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "TEST HD1");
            // Osc1 Zone1: msType=1 (Sample), EXs17 (legacyBankNumber=18), number=199
            WriteHd1Zone(body, 0, 0, 1, LegacyUuid(18), 199);
            // Osc1 Zone2: msType=1, ROM (legacyBankNumber=0) - must be skipped entirely
            WriteHd1Zone(body, 0, 1, 1, LegacyUuid(0), 0);
            // Osc1 Zone3: msType=1, Sampling Mode RAM (legacyBankNumber=1)
            WriteHd1Zone(body, 0, 2, 1, LegacyUuid(1), 2);
            // Osc1 Zone4: msType=2 (Wave Sequence, not a sample ref - ObjectReferenceWalker's own turf)
            WriteHd1Zone(body, 0, 3, 2, LegacyUuid(18), 5);

            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("hd1-exs-row-present", rows.Any(r => r.Description.Contains("EXs17")));
            Check("hd1-exs-row-bucket-is-exs", rows.Any(r => r.Description.Contains("EXs17") && r.Bucket == SampleReferenceWalker.BankBucket.Exs));
            Check("hd1-rom-not-shown", !rows.Any(r => r.Description.Contains("ROM")));
            Check("hd1-ram-row-present", rows.Any(r => r.Description.Contains("Sampling Mode (RAM)")));
            Check("hd1-ram-row-bucket-is-ram", rows.Any(r => r.Description.Contains("Sampling Mode (RAM)") && r.Bucket == SampleReferenceWalker.BankBucket.SamplingModeRam));
            // Zone4 (msType=2, Wave Sequence) reuses EXs17's own UUID on purpose - if the
            // msType filter leaked it through as a sample ref too, this would over-count.
            Check("hd1-row-count-excludes-wave-seq-zone", rows.Count == 2);   // EXs17 + Sampling Mode (RAM) only
        }

        // ── HD-1 Program: Drums-mode zone excluded (ObjectReferenceWalker's own DrumKit ref) ──
        {
            var body = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "TEST DRUMS");
            body[2558] = 4;   // OscMode = Drums
            WriteHd1Zone(body, 0, 0, 1, LegacyUuid(18), 199);   // msType=1, but Drums mode
            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("hd1-drums-zone-excluded", rows.Count == 0);
        }

        // ── HD-1 Program: raw (non-legacy) UUID -> User/3rd-Party bucket, mono/stereo dedupe ──
        {
            var body = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "TEST USR");
            byte[] rawUuid = { 0x91, 0x66, 0xf8, 0x10, 0xd0, 0xc3, 0xbf, 0xb4, 0x6f, 0x89, 0x0a, 0x0c, 0x99, 0x6e, 0x0a, 0x02 };   // stereo bit=0
            byte[] rawUuidStereoSibling = (byte[])rawUuid.Clone();
            rawUuidStereoSibling[15] |= 1;   // same bank, stereo flag set - must dedupe with the mono one
            WriteHd1Zone(body, 0, 0, 1, rawUuid, 2);
            WriteHd1Zone(body, 0, 1, 1, rawUuidStereoSibling, 3);
            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("hd1-raw-uuid-dedupes-stereo-flag", rows.Count == 1);
            Check("hd1-raw-uuid-labeled-user-bank", rows.Count == 1 && rows[0].Description.Contains("User/3rd-Party Sample Bank"));
            Check("hd1-raw-uuid-shows-2x", rows.Count == 1 && rows[0].Description.Contains("(2x)"));
            Check("hd1-raw-uuid-bucket-is-user-bank", rows.Count == 1 && rows[0].Bucket == SampleReferenceWalker.BankBucket.UserOrThirdParty);
        }

        // ── EXi Program: MOD-7/STR-1 PCM OSC High slot, gated on the real engine-type byte
        // (2857, "Algorithm Type" - hardware-confirmed against 3 real test banks spanning all
        // 9 EXi engines, see the investigation capsule) ──
        byte[] exiDefaultBlob = { 0xf4, 0x24, 0x75, 0x04, 0, 0, 0, 0, 0, 0, 0, 0xd0, 0x34, 0x05, 0x00, 0xb0 };
        {
            // MOD-7, High slot on, blob genuinely different from the ROM/default sentinel -
            // real reference, must show.
            var body = new byte[ProgramFormatConverter.WireSizeExi];
            body[2857] = 7;   // Algorithm Type = MOD-7
            body[3375] = 0xb1;
            new byte[] { 0xa1, 0x9a, 0xb6, 0xab, 0x66, 0x43, 0xf0, 0xa4, 0xa4, 0xcb, 0xf2, 0xb9, 0x4f, 0x4d, 0x34, 0x2e }.CopyTo(body, 3376);
            body[3439] = 0x70; body[3440] = 0x0c;   // raw 0x0c70 -> >>4 = 199
            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("exi-mod7-real-ref-shown", rows.Count == 1 && rows[0].Description.Contains("MOD-7") && rows[0].Description.Contains("#199"));
            Check("exi-mod7-bucket-is-exi-external", rows.Count == 1 && rows[0].Bucket == SampleReferenceWalker.BankBucket.ExiExternal);
        }
        {
            // MOD-7, High slot on, but blob is exactly the ROM/default sentinel - must be
            // excluded (matches ROM's own "always there" treatment for the legacy-scheme sites).
            var body = new byte[ProgramFormatConverter.WireSizeExi];
            body[2857] = 7;
            body[3375] = 0xb1;
            exiDefaultBlob.CopyTo(body, 3376);
            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("exi-mod7-rom-excluded", rows.Count == 0);
        }
        {
            // Same bytes as the real MOD-7 hit above, but Algorithm Type says CX-3 (a non-PCM
            // engine) - must NOT be read as a sample reference at all (the whole reason for
            // gating on byte 2857 in the first place).
            var body = new byte[ProgramFormatConverter.WireSizeExi];
            body[2857] = 3;   // CX-3
            body[3375] = 0xb1;
            new byte[] { 0xa1, 0x9a, 0xb6, 0xab, 0x66, 0x43, 0xf0, 0xa4, 0xa4, 0xcb, 0xf2, 0xb9, 0x4f, 0x4d, 0x34, 0x2e }.CopyTo(body, 3376);
            body[3439] = 0x70; body[3440] = 0x0c;
            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("exi-non-mod7-str1-engine-not-walked", rows.Count == 0);
        }
        {
            // STR-1 at its own +74 offset - same formula, different base.
            var body = new byte[ProgramFormatConverter.WireSizeExi];
            body[2857] = 4;   // Algorithm Type = STR-1
            body[3449] = 0xb1;
            new byte[] { 0xa1, 0x9a, 0xb6, 0xab, 0x66, 0x43, 0xf0, 0xa4, 0xa4, 0xcb, 0xf2, 0xb9, 0x4f, 0x4d, 0x34, 0x2e }.CopyTo(body, 3450);
            body[3513] = 0x40; body[3514] = 0x00;   // raw 0x0040 -> >>4 = 4
            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("exi-str1-real-ref-shown", rows.Count == 1 && rows[0].Description.Contains("STR-1") && rows[0].Description.Contains("#4"));
        }

        // ── Drum Kit: dedupe across many zones sharing one bank, on/off gate respected ──
        {
            var body = DrumKitBody.WriteName(new byte[38424], "TEST KIT");
            WriteDrumKitZone(body, 0, 0, true, LegacyUuid(18), 1);
            WriteDrumKitZone(body, 0, 1, true, LegacyUuid(18), 2);
            WriteDrumKitZone(body, 0, 2, true, LegacyUuid(18), 3);
            WriteDrumKitZone(body, 1, 0, false, LegacyUuid(18), 4);   // off - must not count
            WriteDrumKitZone(body, 2, 0, true, LegacyUuid(0), 5);     // ROM - must not show
            var rows = SampleReferenceWalker.Walk(LibObj.DrumKit, body);
            Check("drumkit-single-row-for-shared-bank", rows.Count == 1);
            Check("drumkit-dedupe-count-3x", rows.Count == 1 && rows[0].Description.Contains("(3x)"));
            Check("drumkit-rom-excluded", !rows.Any(r => r.Description.Contains("ROM")));
        }

        // ── Wave Sequence: Multisample-type steps only, Rest/Tie ignored ──
        {
            var body = WaveSequenceBody.WriteName(new byte[2216], "TEST WSEQ");
            WriteWaveSeqStep(body, 0, 0, LegacyUuid(18), 10);    // Multisample
            WriteWaveSeqStep(body, 1, 1, LegacyUuid(18), 11);    // Rest - must be ignored
            WriteWaveSeqStep(body, 2, 2, LegacyUuid(18), 12);    // Tie - must be ignored
            var rows = SampleReferenceWalker.Walk(LibObj.WaveSequence, body);
            Check("waveseq-only-multisample-steps", rows.Count == 1);
            Check("waveseq-exs-label", rows.Count == 1 && rows[0].Description.Contains("EXs17"));
        }

        // ── Init objects reference nothing (mirrors ObjectReferenceWalker.Walk) ──
        {
            var body = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "Init Program");
            WriteHd1Zone(body, 0, 0, 1, LegacyUuid(18), 199);
            var rows = SampleReferenceWalker.Walk(LibObj.Program, body);
            Check("init-program-no-sample-rows", rows.Count == 0);
        }

        return fails;
    }
}
