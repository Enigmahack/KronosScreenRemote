GOAL: Add Drum Kit / Wave Sequence / Global-settings support to KronosScreenRemote's .pcg
parser + registry (Core/Pcg/PcgObjectExtractor.cs, Core/LocalLibrary/ObjectTypeRegistry.cs
etc.), per user's ask to "edit/maintain" these via PCG. Phase 1 (model/registry layer,
synthetic-fixture-only) is CODE-COMPLETE, uncommitted. Phase 2 (this capsule's reason for
being): validate against REAL Kronos-hardware .pcg files just discovered at
"Z:\PCG EXAMPLES\DC BANDS\" (subfolders BBPB, BJBA-TRIBUTE, MONKEYTOWN, RANDOMZ-90's,
RANDOMZ-90_PT2, RANDOMZ-CORPSHOW, RANDOMZ-ROCK, SOUL JESTER - ignore _WORKING and
HARDWARE_TEST_SET, user said those were prior test scratch). NOT yet opened/inspected this
session - just learned of their existence.

STATE (Phase 1, uncommitted - `git status` in Z:\KronosScreenRemote confirms 14 modified +
2 new files, all on top of clean HEAD 7b4a8cb):
- Core/Pcg/PcgObjectExtractor.cs: added DBK1/WBK1/GLB1 to BankChunkObjType; new
  DecodeDrumKitOrWaveSeqBank(bankIdRaw) (Int raw=0 -> objBank 0, User raw=0x20000+N ->
  objBank 0x40+N, N=0..13) - separate from DecodeProgramObjBank/DecodeCombiBankIndex per
  design. count bound widened 1..128 -> 1..200 (WBK1 Int count=150 real per doc corpus).
  Global reuses SetList's unconditional "objBank=0" branch (ignores header's own bankId).
  ReadRecordName: DrumKit/WaveSequence -> DrumKitBody/WaveSequenceBody.ReadName (both new,
  1-liner wrapping Librarian.ReadName - name at offset 0, 24B ASCII, same as
  Program/Combi). Global -> "" always (doc: GLB1 record has NO name at +0).
- Core/LibrarianModel.cs: LibObj.DrumKit=0x04, LibObj.WaveSequence=0x05 added (Korg's real
  func-0x73 obj-dump IDs, confirmed via Documentation/MIDI implementation/
  KRONOS_MIDI_SysEx.txt:1066-67 and *2's bank table ~line 1103-1110). CurrentObjectVersion:
  DrumKit=3, WaveSeq=1 (from DrumKit.txt/WaveSequence.txt headers). ObjLoc.Label() and
  RescanScope.Describe()/BankLabel() got explicit branches (were silently mislabeling as
  Combi/"Set List"). LibObj.Global's comment now explicitly warns: PcgObjectExtractor DOES
  emit a Global ObjLoc now, but ObjectTypeRegistry.Get(LibObj.Global) still throws (NO
  descriptor registered for it - deliberate, see below).
- Core/KronosBanks.cs: added DrumKitLabel/WaveSeqLabel/IsReadOnlyDrumKitBank (bank 0=INT,
  DrumKit-only 0x10=GM read-only, 0x40-0x4D=USER A..GG(14) both types - from
  KRONOS_MIDI_SysEx.txt *2, NOT the same numbering as the .pcg bankId raw field above).
- Core/LocalLibrary/ObjectTypeRegistry.cs: IObjectTypeDescriptor.SlotCount changed from
  `int SlotCount { get; }` (flat) to `int SlotCount(int bank)` (per-bank) - REQUIRED because
  Drum Kit Int=40/User=16 and Wave Seq Int=150/User=32 (pcg_file_format.md §6/§7 corpus,
  NOT independently doc-confirmed for the LIVE MIDI side - see GOTCHAS). All 3 old
  descriptors + 2 new (DrumKitDescriptor, WaveSequenceDescriptor) updated. ~12 call sites
  across LocalEditOps.cs/LocalLibraryCache.cs/LibraryPullPipeline.cs/
  LocalLibraryPaneViewModel.cs/LibrarianShellViewModel.cs/MergeAutoFillSelfTests.cs all
  converted `descriptor.SlotCount` -> `descriptor.SlotCount(bank)` using each site's
  already-in-scope bank variable (verified no site lacked one).
  DrumKit/WaveSequence ARE now registered (user explicitly chose "also wire into
  ObjectTypeRegistry" over "parsing-layer-only" when asked) - this puts them into
  LibraryPullPlanner.AllBanks() (Sync Library pull scope: was 35 banks, now 65) AND makes
  them valid PlaceFromPcg/BatchPlaceFromPcg destinations. Global is explicitly NOT
  registered (doc: "never catalogued, moved, placed, pushed"; payload-base offset
  unresolved - see GOTCHAS).
- Core/ObjectBody/DrumKitBody.cs, WaveSequenceBody.cs: new, ReadName-only (no zone/step
  decoders - nothing consumes them yet).
- Core/BatchMoveModel.cs: typeTag/typeNoun switches (BackupLabel, preview text) got
  DrumKit/WaveSequence arms (were silently falling into the SetList/"set lists" case).
- Self-tests updated to match: Core/ObjectBody/ObjectBodySelfTests.cs
  ("registry-all-three"->"registry-all-five" + new bank/slotcount checks),
  Core/Pcg/PcgFileSelfTests.cs (DBK1/WBK1/GLB1 fixtures added to BuildBankIdEncodedPcg +
  new BuildWideCountWaveSeqPcg for the count=150 case + ~10 new Check() calls),
  Core/LocalLibrary/LocalLibrarySelfTests.cs ("registry-bank-count" 20+14+1 ->
  20+14+1+15+15 to match the new 65-bank pull scope).
- Verification run THIS session (before real files were mentioned): `dotnet build ... -c
  Debug -v quiet -clp:"WarningsOnly;ErrorsOnly;Summary"` clean (0 errors, same 8
  pre-existing CS0067 warnings in Tools/UiThemeSmokeTest.cs, unrelated).
  `--librarian-selftest` -> exit 0 (all pass, incl. the widened-bound/bankid/global fixture
  checks above). `--ui-theme-smoketest` -> exit 0. All against SYNTHETIC fixtures only -
  no real .pcg existed on this machine until the user's message that started Phase 2.
- User declined to commit Phase 1 yet ("No, leave uncommitted") - still sitting as
  uncommitted working-tree changes, 14 modified + 2 new files.

CONFIRMED (Phase 1 design facts, doc/hardware-sourced, still valid going into Phase 2):
- Obj-dump IDs: Drum Kit=0x04, Wave Seq=0x05 (KRONOS_MIDI_SysEx.txt, primary source, not
  inferred).
- .pcg bankId raw-field scheme for DBK1/WBK1 (Int=0, User=0x20000+N) is a DIFFERENT
  numbering than the live obj-dump bank byte (0=Int, 0x10=GM read-only Drum-Kit-only,
  0x40-0x4D=User) - PcgObjectExtractor's DecodeDrumKitOrWaveSeqBank converts raw->obj-dump
  bank directly so ObjLoc values match what ObjectTypeRegistry considers editable.
- Per-bank slot counts (DrumKit 40/16, WaveSeq 150/32) are PCG-CHUNK-COUNT facts
  (pcg_file_format.md corpus, 32 real files, different session/tool than this one) --
  encoded as this codebase's SlotCount(bank), which ALSO drives the live MIDI
  DumpBankBulkAsync/per-slot-sweep loop. Never independently confirmed that live bank sizes
  equal file-declared bank sizes - plausible (itemSize already matches documented Object
  Version dump size exactly) but NOT verified against real hardware traffic.
- GLB1 payload base offset (whether record+0 == wire dump's own +0, vs +12/+16 leading
  sub-header) is explicitly UNRESOLVED per kronosology's own doc ("deferred to Phase B").
  This is why GlobalBody.ReadCategoryNames is deliberately NOT wired to the PCG-extracted
  Global body this pass - a wrong base would produce plausible-looking WRONG category
  names, not a visible failure.

OPEN (ranked, what Phase 2 should resolve using the newly-found real files):
1. Does DKT1 nest DBK1 / WSQ1 nest WBK1, exactly like PRG1/MBK1-PBK1? (assumed by symmetry
   last session, never directly re-derived against a real file - the flat-scan extractor
   doesn't care about nesting correctness per se, but worth confirming tag co-occurrence).
2. Do real DBK1/WBK1 chunks' declared count/itemSize/bankId fields actually match this
   session's assumptions (DrumKit itemSize=38424 count Int=40/User=16; WaveSeq
   itemSize=2216 count Int=150/User=32; bankId raw scheme Int=0/User=0x20000+N)? This is
   the single highest-value check - it's the thing Phase 1 could only assume from a
   different-session's corpus notes in the doc, not verify itself.
3. Does GLB1 in a real file actually carry the same 24-byte bank-chunk
   header+count+itemSize+bankId+checksum-at-+11 shape TryReadBank assumes, or something
   else? (doc's own top-level chunk-directory dump showed GLB1 size=0x6084=24708 exactly,
   which is suggestive but not conclusive - see prior capsule's reasoning, not
   independently re-derived this session).
4. Does the checksum (§12, offset+11 = sum(payload+12..recordsEnd) mod 256) actually
   validate clean on all three new chunk types across the DC BANDS files, or does the
   ChecksumWarnings diagnostic fire (which would be fine/expected per its own "advisory,
   stale checksums happen" design, but worth knowing)?
5. Do any of the 8 real folders (BBPB, BJBA-TRIBUTE, MONKEYTOWN, RANDOMZ-90's,
   RANDOMZ-90_PT2, RANDOMZ-CORPSHOW, RANDOMZ-ROCK, SOUL JESTER) actually contain nonzero
   Drum Kit / Wave Sequence content, or are they Program/Combi-only banks with empty/init
   DBK1/WBK1 chunks? (affects whether names/checksums have anything real to check).

NEXT: List the actual files inside each DC BANDS subfolder (`ls` each, expect *.PCG or
similar), pick one or two real files, run them through PcgFile.Open (e.g. via
Tools/PcgRefDump.cs pattern, or a small ad-hoc headless harness / --librarian-selftest-style
CLI hook) and inspect RejectedBanks/ChecksumWarnings/Objects for DBK1/WBK1/GLB1 entries
specifically. Compare extracted count/itemSize/bankId-decode against OPEN #2's assumptions.
Fix PcgObjectExtractor.cs if real data disagrees with the synthetic-fixture assumptions -
prioritize this over anything else, since Phase 1's correctness entirely rests on those
now-checkable assumptions.

GOTCHAS:
- Don't re-litigate the Phase-1 architecture (SlotCount(bank) refactor, LibObj IDs,
  ObjectTypeRegistry wiring) - that's user-approved and self-test-clean; Phase 2 is
  strictly "does it match real bytes," not a redesign.
- Global must stay OUT of ObjectTypeRegistry regardless of what Phase 2 finds about GLB1's
  structure - even if the payload base gets resolved, wiring it as a placeable/pushable
  type was explicitly descoped (doc's own design says never catalogued/moved/placed/
  pushed) and the user's "also wire into ObjectTypeRegistry" answer was asked and given
  specifically about Drum Kit/Wave Sequence, not Global.
- If real-file inspection finds the count/itemSize/bankId numbers DON'T match what's
  encoded (SlotCount 40/16/150/32, the DecodeDrumKitOrWaveSeqBank formula, the count<=200
  bound), fix Core/LocalLibrary/ObjectTypeRegistry.cs and
  Core/Pcg/PcgObjectExtractor.cs directly - don't just patch around a mismatch, the
  synthetic fixtures encode assumptions, real files are ground truth.
- No project fact-store (bridge.py-style) exists in this repo - this scratch file is the
  durable handoff artifact, same as last session's capsule at
  Z:\KronosScreenRemote\scratch_debug\pcg-drumkit-waveseq-global-capsule.md (that one is
  now largely superseded/DONE - Phase 1 it described is complete).
- Session was at ~121% of context budget when this capsule was written (user explicitly
  chose "write capsule, then continue" over "just continue") - likely to compact or get
  handed off soon; this file plus a fresh `git diff` against HEAD 7b4a8cb is the fastest
  way to rehydolarify Phase 1's exact state without re-reading every file.
