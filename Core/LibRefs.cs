namespace KronosScreenRemote;

// Read/patch the (bank, number) reference bytes inside a DECODED object body.
static class LibRefs
{
    public const int TimbreCount = 16;
    const int Timbre0Num  = 4802;   // timbre 0 program NUMBER byte
    const int Timbre0Bank = 4803;   // timbre 0 program BANK byte (internal linear)
    const int TimbreStride = 188;

    // Set-list slot layout mirrors SetListData.
    const int SlBase = 24, SlStride = 542;
    const int SlTypeOfs = 24, SlBankOfs = 25, SlIndexOfs = 26;
    public const int SlSlotCount = 128;

    // ── Combi timbre → program ──
    public static (int Bank, int Number) CombiTimbreRef(byte[] body, int timbre)
    {
        int b = Timbre0Num + timbre * TimbreStride;
        return (body[b + 1], body[b]);              // bank @ +1 (4803), number @ +0 (4802)
    }

    public static void SetCombiTimbreRef(byte[] body, int timbre, int func33Bank, int number)
    {
        int b = Timbre0Num + timbre * TimbreStride;
        body[b]     = (byte)(number & 0x7F);
        body[b + 1] = (byte)(func33Bank & 0x7F);
    }

    public static IEnumerable<(int T, int Bank, int Number)> IterCombiTimbreRefs(byte[] body)
    {
        for (int t = 0; t < TimbreCount; t++)
        {
            int b = Timbre0Num + t * TimbreStride;
            if (b + 1 >= body.Length) yield break;   // truncated/short dump - stop, don't throw
            var (bank, num) = CombiTimbreRef(body, t);
            yield return (t, bank, num);
        }
    }

    // ── Set-list slot → object ── (type: 0=Combi, 1=Prog, 2=Song)
    public static (int Type, int Bank, int Index) SetListSlotRef(byte[] body, int slot)
    {
        int b = SlBase + slot * SlStride;
        return (body[b + SlTypeOfs] & 0x03, body[b + SlBankOfs] & 0x1F, body[b + SlIndexOfs]);
    }

    // Patch a slot's reference in place, preserving the color/transpose bits that
    // share the type/bank bytes. type == null keeps the existing type bits.
    public static void SetSetListSlotRef(byte[] body, int slot, int func33Bank, int index, int? type)
    {
        int b = SlBase + slot * SlStride;
        if (type.HasValue)
            body[b + SlTypeOfs] = (byte)((body[b + SlTypeOfs] & ~0x03) | (type.Value & 0x03));
        body[b + SlBankOfs]  = (byte)((body[b + SlBankOfs] & ~0x1F) | (func33Bank & 0x1F));
        body[b + SlIndexOfs] = (byte)(index & 0xFF);
    }

    public static IEnumerable<(int S, int Type, int Bank, int Index)> IterSetListSlotRefs(byte[] body)
    {
        for (int s = 0; s < SlSlotCount; s++)
        {
            int b = SlBase + s * SlStride;
            if (b + SlIndexOfs >= body.Length) yield break;
            var (t, bk, ix) = SetListSlotRef(body, s);
            yield return (s, t, bk, ix);
        }
    }

    // ── Program -> Drum Track (another Program) ── Prog_HD-1.txt/Prog_EXi_Common.txt agree on
    // this offset - Drum Track lives in the Common section shared by both wire formats.
    const int DrumTrackNum = 2688;
    public const int ProgramDrumTrackBank = 2689;
    const int DrumTrackOnByte = 1295, DrumTrackOnBit = 0x10;

    // A freshly-created/never-touched Program's Drum Track Bank/Number bytes default to 0,0 -
    // a technically valid-looking Program I-A:000 address, not an absent reference. Gating on
    // "Drum Track On" (its own bit in the same Common byte) is what tells the two apart, the
    // same way a blank Set List slot is skipped via SetListSlot.IsEmpty rather than walked.
    public static bool ProgramDrumTrackOn(byte[] body) => (body[DrumTrackOnByte] & DrumTrackOnBit) != 0;

    public static (int Bank, int Number) ProgramDrumTrackRef(byte[] body) => (body[ProgramDrumTrackBank], body[DrumTrackNum]);

    public static void SetProgramDrumTrackRef(byte[] body, int func33Bank, int number)
    {
        body[DrumTrackNum]        = (byte)(number & 0x7F);
        body[ProgramDrumTrackBank] = (byte)(func33Bank & 0x1F);
    }

    // ── HD-1 Program oscillator zone -> Wave Sequence / Drum Kit (linear-addressed) ── EXi
    // Program bodies don't have this OSC1/OSC2 zone layout - callers gate on wire format
    // (ProgramFormatConverter.WireSizeHd1) before iterating. See
    // KronosBanks.DrumKitLinearToLoc/WaveSeqLinearToLoc for what the Number field means.
    public const int ZonesPerOsc = 8;
    const int Osc1ZoneBase = 2774, Osc2ZoneBase = 3240, ZoneStride = 22, ZoneNumOffset = 18;
    const int OscModeOffset = 2558;

    public static int ProgramOscillatorMode(byte[] body) => body[OscModeOffset] & 0x07;

    public static IEnumerable<(int Osc, int Zone, int MsType, int Number)> IterProgramZoneRefs(byte[] body)
    {
        for (int osc = 0; osc < 2; osc++)
        {
            int oscBase = osc == 0 ? Osc1ZoneBase : Osc2ZoneBase;
            for (int zone = 0; zone < ZonesPerOsc; zone++)
            {
                int typeOff = oscBase + zone * ZoneStride;
                int numOff = typeOff + ZoneNumOffset;
                if (numOff + 1 >= body.Length) yield break;
                yield return (osc, zone, body[typeOff] & 0x03, body[numOff] | (body[numOff + 1] << 8));
            }
        }
    }

    public static void SetProgramZoneNumber(byte[] body, int osc, int zone, int newNumber)
    {
        int numOff = (osc == 0 ? Osc1ZoneBase : Osc2ZoneBase) + zone * ZoneStride + ZoneNumOffset;
        body[numOff]     = (byte)(newNumber & 0xFF);
        body[numOff + 1] = (byte)((newNumber >> 8) & 0xFF);
    }

    // ── HD-1 Program oscillator zone -> actual PCM sample (Bank UUID + Number) ── the
    // zone-type==1 ("Sample") case that ISN'T a Drums-mode zone (msType==1 && oscMode in
    // {4,5} is the Drum Kit case IterProgramZoneRefs/ObjectReferenceWalker already handle as
    // a linear-addressed object ref - excluded here so the same zone is never reported under
    // two different reference kinds). UUID at zone_base+1..+16 (16 bytes), 1 reserved byte at
    // zone_base+17, Number (LE u16) at zone_base+18/19 - hardware-confirmed this session, see
    // SampleReferenceWalker's own header comment for the corpus evidence.
    public static IEnumerable<(int Osc, int Zone, byte[] Uuid, int Number)> IterProgramSampleZoneRefs(byte[] body)
    {
        int oscMode = ProgramOscillatorMode(body);
        for (int osc = 0; osc < 2; osc++)
        {
            int oscBase = osc == 0 ? Osc1ZoneBase : Osc2ZoneBase;
            for (int zone = 0; zone < ZonesPerOsc; zone++)
            {
                int typeOff = oscBase + zone * ZoneStride;
                int numOff = typeOff + ZoneNumOffset;
                if (numOff + 1 >= body.Length) yield break;
                int msType = body[typeOff] & 0x03;
                if (msType != 1 || oscMode is 4 or 5) continue;
                var uuid = body[(typeOff + 1)..(typeOff + 17)];
                int number = body[numOff] | (body[numOff + 1] << 8);
                yield return (osc, zone, uuid, number);
            }
        }
    }

    // ── EXi Program (MOD-7 / STR-1 "PCM OSC" High slot) -> actual PCM sample ── hardware-
    // confirmed this session against real Kronos-saved test programs (see
    // scratch_debug/pcg-sample-dependency-investigation.md for the full derivation).
    //
    // Of the 9 EXi engines, ONLY MOD-7 and STR-1 have any PCM-referencing component at all
    // (confirmed via Documentation/MIDI implementation/Prog_EXi.txt - no other engine's
    // section mentions "MS Bank UUID").
    //
    // WHICH engine a given EXi record uses is read from byte 2857 - "EXi1 Common - Algorithm
    // Type - 00~09 - Off~EP-1" per Documentation/MIDI implementation/Prog_EXi_Common.txt
    // (line 2396), matching Prog_EXi.txt's own per-engine section numbering exactly
    // (2=AL-1, 3=CX-3, 4=STR-1, 5=MS-20EX, 6=PolysixEX, 7=MOD-7, 8=SGX-2, 9=EP-1).
    // Hardware-confirmed against 3 independent real test banks spanning all 9 engines
    // (ZTEST-SMPDEP.PCG/v2/v3): byte 2857 read exactly 7 on every MOD-7 record and exactly 4
    // on every STR-1 record, zero exceptions, including the other 6 engines' own fresh init
    // patches (2,3,5,6,8,9 respectively) - this is the missing discriminator an earlier pass
    // this session searched for in the Common section and didn't find (the field's own name,
    // "Algorithm Type", wasn't an obvious grep target). Corpus-measured before trusting it:
    // with byte 2857 gating engine, and SampleReferenceWalker's own blob-vs-default check
    // still applied on top, the combination produced ZERO false-positive rows across all
    // 18,268 real MOD-7 and 18,286 real STR-1 records in the full 122-file real corpus (see
    // the investigation capsule for the exact numbers) - not just "low," measured zero.
    //
    // Only the "High" PCM OSC slot's layout is confirmed - MidHigh/MidLow/Low were never
    // populated in the real test bank, so their exact byte boundaries are unconfirmed and
    // deliberately not read here.
    const int ExiAlgorithmTypeOffset = 2857;
    const int ExiAlgorithmMod7 = 7, ExiAlgorithmStr1 = 4;
    const int Mod7PcmHighFlagOffset = 3375, Mod7PcmHighUuidOffset = 3376, Mod7PcmHighNumberOffset = 3439;
    const int Str1PcmHighFlagOffset = 3449, Str1PcmHighUuidOffset = 3450, Str1PcmHighNumberOffset = 3513;

    public static IEnumerable<(string Engine, byte[] Blob, int RawNumber)> IterExiPcmHighSlotCandidates(byte[] body)
    {
        if (ExiAlgorithmTypeOffset >= body.Length) yield break;
        var site = body[ExiAlgorithmTypeOffset] switch
        {
            ExiAlgorithmMod7 => ("MOD-7", Mod7PcmHighFlagOffset, Mod7PcmHighUuidOffset, Mod7PcmHighNumberOffset),
            ExiAlgorithmStr1 => ("STR-1", Str1PcmHighFlagOffset, Str1PcmHighUuidOffset, Str1PcmHighNumberOffset),
            _ => ((string Engine, int FlagOff, int UuidOff, int NumOff)?)null,
        };
        if (site is not { } s) yield break;
        if (s.NumOff + 1 >= body.Length) yield break;
        if ((body[s.FlagOff] & 0x01) == 0) yield break;   // "MS Type" On/Off bit for this slot
        var blob = body[s.UuidOff..(s.UuidOff + 16)];
        int raw = body[s.NumOff] | (body[s.NumOff + 1] << 8);
        yield return (s.Engine, blob, raw);
    }

    // Which of the 9 EXi engines (plus "Off") this EXi Program record is set to - same
    // ExiAlgorithmTypeOffset/00~09 field as IterExiPcmHighSlotCandidates above, just exposed for
    // display/search instead of gated down to the 2 PCM-capable engines. Names match
    // Documentation/MIDI implementation/Prog_EXi.txt's own per-engine section numbering (1:HD-1,
    // 2:AL-1, 3:CX-3, 4:STR-1, 5:MS-20EX, 6:PolysixEX, 7:MOD-7, 8:SGX-2, 9:EP-1) and
    // Prog_EXi_Common.txt line 2396's "00~09 - Off~EP-1" range for 0. Only meaningful for an EXi
    // (MBK1) Program record - callers gate on PcgObjectEntry.IsExi first, same as
    // ProgramFormatConverter's own HD-1/EXi split; an HD-1 body has no such field at all.
    public static string? ProgramEngineName(byte[] body) =>
        ExiAlgorithmTypeOffset >= body.Length ? null : body[ExiAlgorithmTypeOffset] switch
        {
            0 => "Off",
            1 => "HD-1",
            2 => "AL-1",
            3 => "CX-3",
            4 => "STR-1",
            5 => "MS-20EX",
            6 => "PolysixEX",
            7 => "MOD-7",
            8 => "SGX-2",
            9 => "EP-1",
            _ => null,
        };

    // Applies a resolved dependency's new (bank, number) at the site Walk/LibraryCatalog.
    // ReferrersOf reported it from - the shared patch step DependencyScanner.
    // RepointPcgReferences, MergeCache.ResolveReferencesForPlacement, Librarian.PlanMove and
    // BatchMoveModel all need once a reference resolves to a real destination.
    // Returns false when the osc-zone branch can't encode the destination (a Drum Kit/Wave
    // Sequence bank outside the linear maps) and therefore wrote NOTHING - callers that record
    // the patch as a real edit must not treat that as a resolved reference. The three plan-
    // building callers ignore it: an unencodable target there simply leaves the old bytes, same
    // as before. Only LocalEditOps.RepatchReference acts on it.
    public static bool ApplyResolvedRef(byte[] body, RefKind refKind, int site, int targetObjType, int destBank, int destNumber)
    {
        if (refKind == RefKind.CombiTimbre)
        {
            SetCombiTimbreRef(body, site, KronosBanks.ObjBankToFunc33(1, destBank), destNumber);
        }
        else if (refKind == RefKind.DrumTrack)
        {
            SetProgramDrumTrackRef(body, KronosBanks.ObjBankToFunc33(1, destBank), destNumber);
        }
        else if (refKind == RefKind.OscZone)
        {
            int? linear = targetObjType == LibObj.WaveSequence
                ? KronosBanks.WaveSeqLocToLinear(destBank, destNumber)
                : KronosBanks.DrumKitLocToLinear(destBank, destNumber);
            if (linear is not { } lin) return false;
            SetProgramZoneNumber(body, site / ZonesPerOsc, site % ZonesPerOsc, lin);
        }
        else
        {
            int refType = ObjectTypeRegistry.Func33RefType(targetObjType) ?? 0;   // a Set List slot can target either
            SetSetListSlotRef(body, site, KronosBanks.ObjBankToFunc33(refType, destBank), destNumber, type: null);
        }
        return true;
    }
}
