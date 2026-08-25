GOAL: Add Drum Kit / Wave Sequence / Global-settings handling to KronosScreenRemote's
.pcg parser (Core/Pcg/PcgObjectExtractor.cs etc.), currently Program/Combi/SetList only.
Design it behind an extensibility seam since Korg Nautilus support is a planned future
target (different model byte at file offset 0x04, likely different bank counts/offsets).
UI wiring is explicitly deferred - this pass is model/parsing layer only.

STATE:
- Core/Pcg/PcgObjectExtractor.cs: flat byte-scan extractor, validates count(1..128)/
  itemSize(64..200000)/bankId before trusting a candidate tag match. Currently only
  scans MBK1/PBK1(Program)/CBK1(Combi)/SBK1(SetList) via `BankChunkObjType` dict.
  24-byte bank-chunk header: +0 tag, +4 declared size(BE,unused), +8 reserved/checksum
  dword(BE), +0xC count(BE), +0x10 itemSize(BE), +0x14 bankId(BE), +0x18 first record.
- Core/Pcg/PcgFile.cs: PcgFile.Open(data) validates KORG magic + model byte(0x04=0x68
  Kronos) + filetype byte(0x05=0x00 PCG), calls PcgObjectExtractor.Extract, exposes
  Objects/RejectedBanks/ChecksumWarnings.
- Core/LocalLibrary/ObjectTypeRegistry.cs: IObjectTypeDescriptor per LibObj type
  (ProgramDescriptor/CombiDescriptor/SetListDescriptor), EditableBanks()/ReadOnlyBanks()/
  BankLabel()/SlotCount - comment says "extension seam for future object types
  (DrumKits, Wave Sequences...) - adding one means writing one new
  IObjectTypeDescriptor + one new Core/ObjectBody decoder, without touching
  LibrarianModel.cs/BatchMoveModel.cs/any registry-driven UI code." This registry is
  for the LIVE SysEx librarian path though, not .pcg - a parallel/adapted concept
  needed for .pcg-only types.
- ViewModels/PcgPaneViewModel.cs: read-only tree view over PcgLibraryView, groups by
  LibObj type via ObjectTreeScaffold - will need new roots for DrumKit/WaveSequence/
  Global once UI is wired (deferred per user).
- kronosology/docs/interfaces/pcg_file_format.md is the authoritative, corpus-verified
  (32 real Kronos OS-3.x files) doc. Just finished a review+fix pass on the Program/
  Combi/SetList extractor against it; all offsets already matched, added §12 checksum
  as an ADVISORY (never rejects) diagnostic - PcgChecksumWarning record, surfaced via
  PcgFile.ChecksumWarnings, logged in PcgPaneViewModel.Load like RejectedBanks.
  PcgFileSelfTests.cs/PcgPaneLoadSelfTests.cs fixture builders now write real checksums
  (helper: internal static PcgFileSelfTests.ChunkChecksum(count,itemSize,bankId,records)).
  Build clean, --librarian-selftest OK, --ui-theme-smoketest OK - this part is DONE and
  committed to working tree (not yet git-committed as far as this session knows -
  check `git status`/CLAUDE.md notes, repo has .git).

CONFIRMED (doc, §ref, corpus-verified 2026-08 unless noted):
- DBK1 (Drum Kit) chunk: 24-byte name + 128 Notes x 300 bytes = 38424 bytes/record.
  Note = 8 Zones x 34 bytes + 7x3-byte crossfade blocks + 7-byte trailer. Zone: +0
  Sample On/Off(1B), +1..~15 Sample Bank UUID(15B), +18 Sample Id(2B LE), +20 Level,
  +21 StartOfs/Reverse, +22 Transpose, +23 Tune, +24 Attack..+32 Low Boost. Corpus:
  itemsize=38424 confirmed on all 32 files, Int bank count=40, 14 user banks count=16
  each -> 15 Drum Kit banks total (matches CPcgSaveInfo setsavedkitbank+_exb(14)).
- WBK1 (Wave Sequence) chunk: 24-byte name + 16-byte Common(offs 24-39) + 64 Steps x
  34 bytes = 2216 bytes/record. Step: +0 Step Type(2 bits: Multisample/Rest/Tie),
  +1..~15 Bank Select UUID, +18 Multisample Select(2B LE), +20..+33 various. Corpus:
  itemsize=2216 confirmed, Int bank count=150, 14 user banks count=32 each -> 15 Wave
  Seq banks total (setsavewseqbank+_exb(14)).
- Both Drum Kit and Wave Sequence use the SAME (Sample Bank UUID[16B], numeric Id[2B])
  reference scheme as Program oscillator zones (§3.3) - one shared Kronos-wide
  convention, not three separate ones. UUID format (§7): legacy ROM/EXs1-126 use fixed
  derivable pseudo-UUID `4B 4F 52 47 00*8 4D 53 00 nn` where nn=(legacy_bank_number<<1)
  | stereo_flag, legacy_bank_number = EXs_number+1; EXs127+/3rd-party use a real stored
  UUID verbatim (byte15 bit0 = mono/stereo flag either way). Multisample/Sample Id
  field is little-endian (confirmed empirically + Korg's docs flag the one BE exception
  elsewhere, implying unmarked = LE).
- GLB1 (Global) chunk: 24708 bytes, ONE record only (singleton, not banked - bank 0
  index 0). NO name at offset+0 (bytes start `00 00 08 02...`) - exact payload base
  (payload+0 vs +12 vs +16) is UNRESOLVED ("deferred to Phase B" in the doc). Category
  tables confirmed at absolute offsets 12912(Program cat)/13344(Program subcat)/
  16800(Combi cat)/17232(Combi subcat), 24-byte ASCII stride, all 32 corpus files.
  Rest of the 24708 bytes (MIDI routing, KARMA, control-surface etc.) NOT catalogued -
  doc says grep Korg's own Global.txt on demand rather than pre-mapping.
- Bank-id encoding for Drum Kit/Wave Sequence (WaveSequenceBankId2WaveSequenceIndex/
  DrumKitBankId2DrumKitIndex, identical formula, doc §2.4): raw bankId 0 = Int (index
  0), raw bankId >= 0x20000 => index = (bankId - 0x20000) + 1 (User A=1,B=2,...).
  DIFFERENT from Program's I-F 0x8000-flag scheme and Combi's plain-linear scheme -
  each of the 4 object types has its own bankId decode rule, do not reuse
  DecodeProgramObjBank/DecodeCombiBankIndex for these two.
- Top-level chunk order (all 32 corpus files, identical): DIV1 -> SLS1 -> PRG1 -> CMB1
  -> DKT1 -> WSQ1 -> GLB1 -> DPI1. DKT1 nests DBK1, WSQ1 nests WBK1 (by analogy with
  PRG1/MBK1-PBK1 and CMB1/CBK1 - not explicitly re-derived this session but consistent
  with doc's general "each top-level chunk nests its own sub-chunk shape" statement).
- §12 checksum (byte at chunk.Offset+11 = sum(payload from +12 to recordsEnd) mod 256)
  is explicitly stated to apply to DBK1/WBK1/GLB1 too ("every PBK1, MBK1, CBK1, SBK1,
  GLB1, WBK1, DBK1 chunk... regardless of whether this project has decoded that
  chunk's contents") - i.e. checksum validation for these three new types should reuse
  the exact same TryReadBank-style logic once wired in, even before body decoding is
  built out.
- PCG-Tools (the reference C# tool) does NOT decode Drum Kit contents at all
  (DrumKit.ChangeReferences throws NotImplementedException) - Korg's own SysEx docs
  (DrumKit.txt/WaveSequence.txt) are the only source for these two, already summarized
  above from pcg_file_format.md §6/§7.
- Model-type byte for extensibility: file offset 0x04, values documented in §2.1:
  Kronos=0x68, Trinity=0x3B, TritonKarma=0x5D, TritonLe=0x63, Oasys=0x70, M3=0x75,
  M50=0x85, MicroStation=0x8D, Krome=0x95, Kross=0x96, Kross2=0xC9, KromeEx=0xD2,
  Triton-family=0x50+sub-byte. NAUTILUS VALUE NOT IN THIS TABLE - doc doesn't cover it,
  will need separate sourcing (Nautilus MIDI implementation SysEx docs, or a real
  Nautilus .pcg file) before an accurate model-byte/bank-count table can be built for
  it. PcgFile.Open currently hardcodes `data[4] != 0x68 => return null` - this is the
  literal chokepoint that would need to become model-aware for multi-model support.

OPEN (ranked):
1. What extensibility interface shape fits "add DrumKit/WaveSequence/Global now,
   Nautilus later" - most likely an IPcgObjectTypeDescriptor (bank-id decode fn,
   itemSize, tag(s), LibObj-equivalent id) driving a still-flat scan, kept SEPARATE
   from the live-SysEx ObjectTypeRegistry (different bankId schemes per type, and PCG
   parsing must stay read-only per existing Core/Pcg design note). Model-specific
   differences (Kronos vs Nautilus) likely need a second axis: which descriptor set
   applies, keyed off the file's model byte at 0x04 - not designed yet.
2. Whether DrumKit/WaveSequence should get full per-zone/per-step body decoders now
   (Core/ObjectBody/DrumKitBody.cs, WaveSequenceBody.cs mirroring ProgramBody/
   CombiBody/SetListBody) or just be extracted+named+checksum-checked first with body
   decode as a follow-up - user said "UI later" but didn't say body-decode later;
   probably still want Name extraction at minimum (Drum Kit/Wave Sequence both have a
   24-byte name at record offset 0, confirmed) since PcgObjectExtractor.ReadRecordName
   needs *something* per type to build tree labels eventually.
3. Global is a singleton (no bank/array) - LibObj/ObjLoc model assumes (ObjType, Bank,
   Number) triples; Global will need Bank=0,Number=0 fixed, same pattern SetList
   already uses for its own "no per-object bank" case (objBank=0 constant in
   TryReadBank's SetList branch) - reuse that pattern, not a new one.
4. Whether new LibObj type constants are needed (grep Core/LibrarianModel.cs `static
   class LibObj` for existing Program/Combi/SetList int values before adding
   DrumKit/WaveSequence/Global ones, to avoid colliding with existing live-SysEx
   LibObj values used elsewhere in the app for the *live* librarian, which never
   modeled these 3 types - doc §9 confirms - so may be free to allocate, but must
   verify no collision and no live-path code implicitly assumes LibObj's small closed
   set of 3).

NEXT: Read Core/LibrarianModel.cs's `static class LibObj` definition + grep the whole
repo for `LibObj\.(Program|Combi|SetList)` usages to find every switch/dictionary that
currently assumes exactly-3-types (ObjectTypeRegistry._byType, PcgObjectExtractor.
BankChunkObjType, ObjectTreeScaffold, ReadRecordName) before writing any new code -
each is a place a naive 4th/5th/6th type addition could be silently ignored or crash.
Then design the extensibility interface (OPEN #1) and get user sign-off on its shape
(likely worth a short design doc or explicit plan-mode pass, NOT jumping straight to
code, given the "rig up extensibility for Nautilus" ask is architecture-sensitive)
before implementing DrumKit/WaveSequence/Global.

GOTCHAS:
- Don't reuse DecodeProgramObjBank/DecodeCombiBankIndex for Drum Kit/Wave Sequence -
  different bankId formula (Int=0, User=bankId-0x20000+1), see CONFIRMED above.
- Global has no per-record name at offset+0 (unlike every other type) - a generic
  ReadRecordName dispatch that defaults to "read name at 0" will silently produce
  garbage for Global; needs its own branch returning "" or a fixed label.
- GLB1 payload base offset is explicitly UNRESOLVED in the doc (Phase B item) - don't
  hardcode a Global field offset table without flagging this uncertainty same as the
  doc does.
- Nautilus model byte is NOT documented anywhere in kronosology docs read this
  session - do not guess/invent a value; either ask the user or explicitly stub the
  interface so a model byte can be added later without a guessed placeholder shipping
  as if verified.
- Checksum validation (PcgChecksumWarning, just built this session) should extend
  naturally to DBK1/WBK1/GLB1 per doc's explicit statement it applies to those too -
  don't reinvent it, reuse ComputeChecksum/the offset+11-vs-payload-sum pattern.
- ObjectTypeRegistry.cs's IObjectTypeDescriptor is for the LIVE SysEx librarian
  (EditableBanks/ReadOnlyBanks drive WRITE paths over MIDI) - do NOT wire .pcg-only
  types into it as if they were live-writable; PCG parsing is read-only-forever per
  PcgLibraryView's own doc comment ("requirement 11... nothing here, or anywhere in
  Core/Pcg, ever writes back into a .pcg file").
- Session was at ~88% context when this capsule was written; user ran /context-capsule
  explicitly rather than /compact - likely wants to hand this off or checkpoint before
  continuing in a fresh context. No project fact-store (bridge.py-style) found in this
  repo; this file is the durable handoff artifact.
