# Commit Notes

Staged/pending work from the Sample Editor feature branch (`feature/sample-editor`),
left uncommitted per request — commit these yourself when ready.

## Overview (paste-able top-level summary)

Add a full Sample Editor tool to KronosScreenRemote: load/edit/save Korg
`.KSC`/`.KMP`/`.KSF` sample content, Audacity-style waveform DSP with undo,
FTP pull/push against a live Kronos, MP3/MP4/WAV import + WAV export, stereo
sample-pair creation, and format-doc updates in the sibling `kronosology`
repo recording everything newly hardware-confirmed along the way.

- **Format layer** (`Core/Sample/*`): direct C# port of the Python reference
  library, byte-identical round-trip against 75 real Kronos fixtures
  (`--sample-format-fixture-check`), plus loop-point/original-key/stereo-pair
  fields that library predates - all hardware-confirmed this session via
  controlled test files built on a real Kronos.
- **Editor window** (`Views/SampleEditorWindow.*`, `ViewModels/
  SampleEditorViewModel.cs`): Collection → Multisample → Zone tree, safe-field
  editing, custom waveform control (drag-select, zoom, playback cursor).
- **Waveform DSP** (`Core/Sample/Dsp/*`): crop/normalize/fade/silence-trim/
  tempo-pitch (SoundTouch.NET), bounded byte-capped undo/redo, WASAPI
  playback incl. loop-preview.
- **FTP** (`ViewModels/IRemoteSampleSource.cs`, `Views/
  KronosRemoteSampleSource.cs`): dependency-closure pull/push (a `.KSC` pull
  fetches every `.KMP` + every non-skipped zone's `.KSF`, mirroring the
  Kronos's own folder convention 1:1), with header-only/dirty push guards.
- **Transcode + stereo** (`Core/Sample/AudioImport.cs`,
  `SampleImportBuilder.cs`, `SampleExport.cs`): WAV/MP3/MP4 → Kronos-native
  mono-or-stereo 44.1kHz import, WAV export (single/multisample/collection),
  and stereo-pair creation grounded in a real discovery this session - a
  Kronos stereo instrument is two complete `.KMP` multisamples (same Name,
  opposite `-L`/`-R` Suffix, adjacent `MNO1`), never two zones in one `.KMP`.
- **Polish**: loop-preview toggle, zone deletion (marks `SKIPPEDSAMPLE`
  rather than restructuring the keymap), Recent Files, multisample/collection
  batch export, sample-rate/bit-depth normalization report.

Three real bugs were found (each by an off-hardware self-test or the
real-fixture E2E smoke test actually exercising the failure path, not by
inspection) and fixed before shipping:
1. Tree-rebuilding ViewModel methods left stale `_selectedNode`/`_selectedZone`
   selections behind, letting a subsequent Save silently act on pre-rebuild
   in-memory state and discard just-written content.
2. `AppSettings.SampleRecentFiles` was silently dropped by both directions of
   `Storage.cs`'s hand-rolled settings.json reader/writer (which special-cases
   every non-primitive field individually) - it never actually persisted.
3. Fixing #2 exposed that every Sample Editor self-test/diagnostic tool was
   about to start silently mutating a real user's actual settings.json the
   moment `--librarian-selftest` ran, via `OpenCollection`'s new Recent-Files
   write. Fixed with snapshot/restore guards around every such run.

All verification suites green throughout: `--librarian-selftest` (dozens of
new self-tests across the format layer, DSP, FTP wiring, transcode, stereo,
and this session's bug regressions), `--ui-theme-smoketest`,
`--sample-format-fixture-check` (75/75 real files, byte-identical), and an
extended `--sample-editor-smoketest` E2E run against real hardware-pulled
fixtures covering the full pipeline: open → edit → DSP+undo → export/import
round-trip → stereo pair creation/import → save/reload persistence.

10 suggested commits below (9 in `KronosScreenRemote`, 1 in `kronosology`) if
splitting by phase is preferred over one squashed commit; each has its own
file list and message.

## `KronosScreenRemote` repo

**1. Phase 0 — add SoundTouch.Net dependency**
Files: `NuGet.config`, `KronosScreenRemote.csproj`
```
Add SoundTouch.Net + SoundTouch.Net.NAudioSupport for independent pitch/tempo DSP

LGPL-2.1-or-later, dynamically linked. Whitelisted both package patterns in
NuGet.config's packageSourceMapping (was blocking restore - only FluentFTP/NAudio/
CommunityToolkit.Mvvm were previously allowed).
```

**2. Phase 0 — gitignore local sample fixtures**
Files: `.gitignore`
```
Ignore SampleFixtures/ - local-only real Kronos sample files

Real .KSC/.KMP/.KSF pulled from the live Kronos for Sample Editor format-layer
validation (fixture-check tool, loop-point/original-key hardware tests). Same
policy as other real-hardware-file fixtures: kept local, never committed.
```

**3. Phase 1 — Core/Sample/* format-layer port**
Files: `Core/Sample/KorgRiffChunk.cs`, `KsfPcm.cs`, `KsfSample.cs`, `KmpZone.cs`,
`KmpMultisample.cs`, `KscCollection.cs`, `SampleSelfTests.cs`,
`Tools/SampleFormatFixtureCheck.cs`, `App.xaml.cs` (self-test wiring +
`--sample-format-fixture-check` flag)
```
Port .KSC/.KMP/.KSF format layer to C#, with loop points + original key

Direct transliteration of Tools/sample_editor/kronos_ksc_format.py, plus the
loop-point (KsfSample.SampleStart/LoopStart/LoopEnd) and original-key
(KmpZone.OriginalKey/TopKey) fields that library predates - both hardware-
confirmed this session. Fixed two real bugs the port surfaced that the Python
reference also has: the 16-byte short name field and 24-byte NAME chunk aren't
simple re-truncations of each other (a name >14 base chars was being silently
truncated on every save), and the KMP RLP1 key fields were mislabeled key_low/
key_high when they're actually Original Key/Top Key. Also preserves the SMF1
chunk and the SMP1 "loop start duplicate" slot verbatim rather than
recomputing them, after real fixtures showed both carry values a naive
re-derivation would corrupt (a corrupted header-only .KSF's stale metadata,
and 5 legacy-rate files whose duplicate slot doesn't mirror Loop Start).

75/75 real Kronos fixtures (pulled from the live unit, gitignored, not
committed) round-trip byte-identical via the new --sample-format-fixture-check
CLI flag.
```

**4. Phase 1 — Sample Editor window (ViewModel/View, disk-only)**
Files: `ViewModels/SampleTreeNode.cs`, `SampleEditorViewModel.cs`,
`Views/SampleEditorWindow.xaml`/`.xaml.cs`, `Views/SampleWaveformControl.cs`,
`Tools/SampleEditorSmokeTest.cs`, `Tools/SampleEditorVisualCheck.cs`,
`Views/MainWindow.xaml`/`.xaml.cs` (menu item + window-opening),
`Tools/UiThemeSmokeTest.cs` (registers the new window), `App.xaml.cs`
(`--sample-editor-smoketest` / `--sample-editor-visual-check` flags),
`Core/Sample/KsfSample.cs` (Open() now requires an SMD1 chunk)
```
Add Sample Editor window - browse/view/edit .KSC/.KMP/.KSF content on disk

Collection -> Multisample -> Zone tree (left) + zone/sample detail with a
custom OnRender waveform control (right), same ThemedWindow/hybrid-MVVM shape
as the rest of the app. Edits the confirmed-safe fields only: zone Original
Key/Top Key, sample rate, loop enable/Sample Start/Loop Start/Loop End - same
scope discipline as the Python POC, now with loop/key editing the POC never
had since those fields weren't confirmed yet when it was built.

Two-pane, not the plan's original three-pane sketch (the tree already carries
Collection/Multisample/Zone in one control, so a separate middle zone-list
pane would just duplicate it) - and doesn't reuse PaneSelection.cs/
PaneInteraction.cs, since this tree has none of the drag-drop/dirty-conflict
complexity those exist for. Both are deliberate scope calls, not oversights.

Fixed two real bugs found while writing this layer: KsfSample.Open() accepted
a truncated file (valid SMP1, no SMD1 at all) and silently produced a
default-valued object indistinguishable from a real header-only-corrupted
.KSF - now requires SMD1 to be present, which matters once Phase 2 pulls
files over FTP (a cut-off transfer is the expected truncation failure mode
there). And "Save Sample"/"Save Multisample" are independent save paths (a
zone's key range lives in the .KMP, a sample's fields live in its own .KSF) -
editing both and saving only one silently dropped the other with no warning;
now tracks per-side dirty state and calls it out in the status text.

FTP pull/push, waveform DSP (crop/tempo/pitch/undo), and format transcoding
are later phases - this is view/edit against local disk only, wired from a
new Tools > Sample Editor... menu item.

New --sample-editor-smoketest <path.ksc> CLI flag drives the real ViewModel
end-to-end against a real fixture (open -> select zone -> confirm waveform
loaded -> edit sample rate -> save -> reopen from disk to verify persistence
-> same for zone key range), mirroring Tools/sample_editor/_gui_smoke_test.py's
rigor for the Python POC. Passes against two real hardware fixtures.

New --sample-editor-visual-check <path.ksc> CLI flag actually shows the real
window and PrintWindow-screenshots it through empty/collection-selected/
multisample-selected/zone-selected/skipped-zone-selected states - caught a
real WrapPanel bug (a field label separating from its textbox onto its own
line, screenshot-only, no automated test would have found it) before this
went out.
```

**5. Phase 3 — Waveform DSP + undo/redo + playback**
Files: `Core/AppSettings.cs` (`SampleUndoByteCapMb`), `Core/Sample/Dsp/ISampleEffect.cs`,
`TempoPitchProcessor.cs`, `CropEffect.cs`, `GainNormalizeEffect.cs`, `FadeEffect.cs`,
`SilenceTrimEffect.cs`, `SampleDspSelfTests.cs`, `Core/Sample/SampleEditUndo.cs`,
`Core/Sample/SamplePlayback.cs`, `ViewModels/SampleEditorViewModel.cs` (undo/redo,
effect-apply funnel, playback, selection state), `Views/SampleEditorWindow.xaml`/`.xaml.cs`
(Edit menu, Play/Stop/Undo/Redo/Crop/Normalize/Trim/Fade/Tempo-Pitch controls, Ctrl+Z/Y),
`Views/SampleWaveformControl.cs` (drag-select, mouse-wheel zoom, double-click reset,
selection highlight), `Tools/SampleEditorSmokeTest.cs` (extended), `App.xaml.cs`
(`SampleDspSelfTests` wiring)
```
Add waveform DSP (crop/normalize/fade/silence-trim/tempo-pitch) + undo/redo + playback

DSP effects go through a uniform ISampleEffect.Apply(short[], sampleRate) shape;
TempoPitchProcessor wraps SoundTouch.SoundTouchProcessor separately since it needs
float conversion at the boundary, not another ISampleEffect. Undo is a bounded
FIFO-eviction snapshot stack keyed on raw byte size (SampleUndoByteCapMb, default
256MB), not a command+inverse journal - most of these edits (crop, tempo/pitch)
have no cheap analytic inverse, and PCM snapshots vary too much in size for a
step-count cap to mean anything. Reset per zone selection.

SampleWaveformControl rewritten from a static OnRender trace into a real
interactive control: click-drag selects a frame range (feeds Crop), mouse wheel
zooms toward the cursor, double-click resets zoom. FrameworkElement has no
OnMouseDoubleClick override (that's Control-only) - double-click is detected via
ClickCount inside OnMouseLeftButtonDown instead.

Playback via SamplePlayback (WasapiOut), same BE/LE-boundary discipline as the
rest of Core/Sample - converts host-order short[] to NAudio's expected bytes,
never touches on-disk KSF bytes directly.

Extended --sample-editor-smoketest to prove undo/redo bit-exact correctness
against a real hardware fixture: crop -> undo -> assert SequenceEqual against
the pre-edit waveform -> redo -> assert cropped state restored, then chains
tempo/pitch, normalize, fade, and silence-trim each followed by undo, ending
on a final bit-exact-original assertion after undoing every edit made.

All green: --librarian-selftest (incl. new SampleDspSelfTests), --ui-theme-
smoketest, --sample-format-fixture-check (still 75/75, confirms no format-layer
regression), --sample-editor-smoketest (full DSP+undo chain, bit-exact).
```

**6. Phase 2 — FTP pull/push + local sample workspace**
Files: `Core/AppSettings.cs` (`SampleWorkspaceRoot`), `Core/Sample/SampleWorkspace.cs`,
`Core/Sample/SampleFtpClosure.cs`, `Core/Sample/SampleRemoteSelfTests.cs`,
`ViewModels/IRemoteSampleSource.cs`, `Views/KronosRemoteSampleSource.cs`,
`Views/SampleRemoteBrowserDialog.xaml`/`.xaml.cs`, `ViewModels/SampleEditorViewModel.cs`
(pull/push methods + remote-path map), `Views/SampleEditorWindow.xaml`/`.xaml.cs`
(File menu: Pull Collection/Multisample from Kronos, Push Sample/Multisample to Kronos),
`Tools/SampleFtpPullCheck.cs`, `App.xaml.cs` (`SampleRemoteSelfTests` wiring +
`--sample-ftp-pull-check` flag), `Core/AppMessages.cs` (`RemoteSamplePicker`)
```
Add FTP pull/push for the Sample Editor - .KSC/.KMP + full dependency closure

IRemoteSampleSource mirrors IRemotePcgSource's seam (production code owns both
untestable halves - login + browse/download - a self-test injects an in-memory
fake), but shaped around a dependency-closure pull instead of a single file:
picking a .KSC downloads it plus every listed .KMP plus every non-skipped
zone's .KSF, replaying the exact same folder convention the format already
uses on both ends (KmpZone.KsfPath) - so every pulled file's local path is
just its full remote path mirrored under the workspace root, and OpenCollection's
own independent path-building finds everything without needing to know a pull
ever happened.

SampleFtpClosure holds the actual closure-walk logic (extracted so it's usable
both from the interactive SampleRemoteBrowserDialog and headlessly from the new
--sample-ftp-pull-check CLI diagnostic, the real-hardware counterpart to
--sample-format-fixture-check - that one only ever reads already-local files).

Push resolves its remote destination from a local-path -> remote-path map built
at pull time; content that was never pulled has no entry and simply can't be
pushed (no path-guessing). Two guards before a push goes out: refuses a dirty
(unsaved) local file (would silently upload the stale pre-edit bytes) and
refuses a header-only (zero-frame) sample outright (doc §3.3's real failure
mode - a corrupted/never-fully-read local sample would otherwise silently
overwrite a good sample on the Kronos).

New --librarian-selftest coverage (SampleRemoteSelfTests) exercises the fake-
source pull/push wiring end to end: successful pull populates the tree, clean
push hits the right remote path, header-only push is refused, dirty-sample
push is refused, never-pulled content is refused, and a cancelled pull leaves
the previously loaded tree untouched.

Real-hardware verification (--sample-ftp-pull-check against the live Kronos)
not run by me this session - no FTP credentials are persisted to disk in this
dev environment (settings.json doesn't exist here), and entering a password
outside the app's own LoginDialog isn't something I should do on your behalf.
Everything up to the wire protocol is verified off-hardware; the actual FTP
round-trip against Store-Bank spike test files (SMPTEST/LOOP.KSC etc.) still
wants a real click-through in the running app.
```

**7. Phase 4 — audio transcode + import/export pipeline**
Files: `Core/Sample/AudioImport.cs`, `SampleImportBuilder.cs`, `SampleExport.cs`,
`SampleTranscodeSelfTests.cs`, `ViewModels/SampleEditorViewModel.cs` (import/export
methods + `RefreshTreeAfterMutation`), `Views/SampleEditorWindow.xaml`/`.xaml.cs`
(File menu: Import Audio, Export Sample to WAV, Export Collection to Folder),
`Tools/SampleEditorSmokeTest.cs` (extended - real export→import round trip),
`App.xaml.cs` (`SampleTranscodeSelfTests` wiring)
```
Add audio import (WAV/MP3/MP4 -> Kronos-native mono/44100) + WAV export

AudioImport decodes via NAudio.Wave.WaveFileReader (WAV, any bit depth) or
MediaFoundationReader (MP3/MP4/M4A/WMA - Windows's own codecs, zero extra
dependency beyond the NAudio package already in use for playback/capture),
then downmixes every channel by averaging (not a left-only drop) and
resamples to 44100 via WdlResamplingSampleProvider when the source rate
differs - the two things NAudio's own codec conversion doesn't already do
for us and this port is actually responsible for getting right.

SampleImportBuilder adds the new zone in TopKey-sorted order (not just
appended past a lower-keyed zone later in the list - KmpZone's own doc
comment: zone order IS key-range order) and writes the .KSF via the same
NextKsfFilename()/folder convention every other zone uses - importing audio
is "build a real zone the way the Kronos itself would," not a special case.

SampleExport's semantics are explicit and one-directional: a .KSF exports to
one WAV, a .KSC bulk-exports every non-skipped zone's .KSF across every .KMP
it lists, named after the sample (not the zone's MSxxxyyy.KSF filename) so
the output folder is actually browsable. Header-only samples are skipped,
never exported as a 0-sample WAV - IsHeaderOnly's fourth consumer, after the
Phase 1 waveform view, Phase 2 FTP push guard, and now this.

MP3/MP4 decode itself can't be exercised in a synthetic self-test (no
in-process encoder, per the original plan) - SampleTranscodeSelfTests instead
covers everything AudioImport is actually responsible for (channel downmix,
resampling) via synthetic WAV, plus SampleImportBuilder's naming/ordering and
a bit-exact SampleExport->AudioImport round trip. The extended
--sample-editor-smoketest goes further: exports a REAL hardware-pulled sample
to WAV, imports it back as a brand-new zone, and asserts the round trip is
bit-exact against a real Kronos fixture (SMPTEST/LOOP.KSC) - not just
synthetic data.

Fixed one real bug this exposed before it shipped: importing rebuilds the
whole tree from disk (same as every other structural mutation), which orphans
any SampleTreeNode/KmpZone a caller was still holding from before the import -
still data-correct, but no longer reference-equal to anything in the current
tree, which would have made a subsequent Save silently fail to find its
owning multisample. The extended smoke test re-resolves its zone node by
filename after the import specifically to catch this, and did.

All green: --librarian-selftest (incl. new SampleTranscodeSelfTests),
--ui-theme-smoketest, --sample-format-fixture-check (still 75/75), extended
--sample-editor-smoketest (full export/import round trip, bit-exact, against
real hardware fixture data).
```

**8. Stereo pair creation/import + fix a real stale-selection data-loss bug**
Files: `Core/Sample/AudioImport.cs` (`ImportStereoToLR44100`/`ConvertToStereo44100`),
`SampleImportBuilder.cs` (`AddStereoSampleZonePair`, `CreateStereoMultisamplePair`,
`FindStereoSibling`, `AddSampleZone` suffix param), `SampleStereoSelfTests.cs`,
`SampleTreeSelectionSelfTests.cs` (new - regression coverage for the bug below),
`ViewModels/SampleEditorViewModel.cs` (`NewStereoMultisamplePairInCollection`,
`ImportStereoAudioAsNewZonePair`, selection-clearing fix in `RebuildTreeFromCollection`/
`RefreshTreeAfterMutation`), `Views/SampleEditorWindow.xaml`/`.xaml.cs` (File menu: New
Stereo Multisample Pair, Import Audio as Stereo Pair), `Tools/SampleEditorSmokeTest.cs`
(extended - real stereo pair round trip against a hardware fixture),
`Tools/SampleStereoScan.cs` (new, one-off - `--sample-stereo-scan <folder>` diagnostic
that grounded this whole feature in real Kronos-authored data), `App.xaml.cs`
(`SampleStereoSelfTests`/`SampleTreeSelectionSelfTests` wiring + `--sample-stereo-scan`
flag)
```
Add stereo pair creation/import + fix a stale-selection bug it exposed

Before writing any code, scanned every real .KMP fixture pulled this session
through the actual format layer (Tools/SampleStereoScan.cs - deliberately not
a raw byte grep, which false-positives constantly against PCM data that
happens to contain "-L"/"-R" byte sequences) to find out what a real Kronos
stereo instrument actually looks like on disk, rather than guessing. Result,
now documented in kronosology's ksc_kmp_ksf_file_format.md §2.2: a stereo
instrument is TWO complete multisamples (.KMP files) - same Name, opposite
"-L"/"-R" Suffix, adjacent MNO1, matching zone key ranges - never two zones
inside one .KMP (RLP1 has no channel field). Also confirms SMF1 (floated
earlier as a possible stereo-link field, still unconfirmed) plays no role in
real pairs - they link purely through Name+Suffix+MNO1.

AudioImport gained a stereo-preserving decode path alongside the existing
mono downmix: a true stereo source keeps channels separate, a mono source
duplicates into both (builds a real stereo pair from mono material rather
than refusing). SampleImportBuilder gained CreateStereoMultisamplePair (two
linked .KMP files, sequential MNO1, both added to the collection) and
AddStereoSampleZonePair (matching zone in both, -L/-R baked into each side's
own .KSF Name/Suffix per the decompiled MakeNameLeft/MakeNameRight naming).
FindStereoSibling resolves an existing multisample's pair by Name+opposite-
Suffix+adjacent-MNO1 - documented as a known-ambiguous heuristic (the format
has no explicit pairing field), not engineered around further: the same
fixture scan that confirmed the convention also found a real counter-example
(an unrelated unpaired mono multisample sitting MNO1-adjacent to a real
pair) where this heuristic would pick the wrong sibling.

Real bug found via the extended --sample-editor-smoketest (not a synthetic
self-test - this one only showed up driving the real ViewModel against a
real fixture end to end): RebuildTreeFromCollection/RefreshTreeAfterMutation
(called after every tree-changing operation - new multisample, audio import,
stereo pair creation, ...) replace every multisample/zone node with a
freshly-opened-from-disk object, but never cleared the ViewModel's own
_selectedNode/_selectedZone/_selectedSample fields. A stale selection held
across one of these operations could silently drive a subsequent Save onto
pre-rebuild in-memory state - concretely, in the smoke test, creating a
stereo pair while a different zone was selected, then saving, overwrote the
just-created stereo multisample's freshly-imported zone with an empty pre-
import copy. Fixed by clearing selection (SelectNode(null)) at the start of
both tree-rebuild paths, so a stale selection can never drive a save - the
caller must explicitly re-select first, the same as picking a different tree
item, instead of silently reverting a just-made change. New
SampleTreeSelectionSelfTests.cs adds permanent regression coverage for this
exact failure mode (synthetic, off-hardware). This was a pre-existing bug,
not something Phase 4/stereo introduced - NewMultisampleInCollection had the
same exposure since Phase 1, just never got exercised by anything until this
session's stereo E2E test happened to select-then-rebuild-then-save in that
specific order.

All green: --librarian-selftest (incl. new SampleStereoSelfTests and
SampleTreeSelectionSelfTests), --ui-theme-smoketest, --sample-format-fixture-
check (still 75/75), extended --sample-editor-smoketest (real stereo pair
created + imported + verified against a real hardware fixture, plus the
fixed zone-key-edit-persists check that originally caught the bug).
```

**9. Phase 5 — polish: loop preview, zone delete, Recent Files, batch export, normalization report + fix a real settings.json data-loss bug**
Files: `Core/Sample/SamplePlayback.cs` (`PlayLooped`/`LoopingSampleProvider`),
`SampleExport.cs` (`ExportMultisample` refactor), `SampleNormalizationReport.cs` (new),
`SamplePhase5SelfTests.cs` (new), `Core/AppSettings.cs` (`SampleRecentFiles`),
`Core/Storage.cs` (`sample_recent_files` JSON wiring + `RunWithSettingsFileProtected`),
`ViewModels/SampleEditorViewModel.cs` (`PlaySelectedSample` loop mode,
`DeleteSelectedZone`, `ExportSelectedMultisampleToFolder`, `BuildNormalizationReport`,
Recent Files methods), `Views/SampleEditorWindow.xaml`/`.xaml.cs` (Loop Preview
checkbox, Delete Zone button/menu/shortcut, Space-to-play shortcut, Recent Files
submenu, Export Multisample/Normalization Report menu items),
`Views/SampleNormalizationReportWindow.xaml`/`.xaml.cs` (new),
`Tools/SampleEditorSmokeTest.cs` + `SampleEditorVisualCheck.cs` (settings.json
snapshot/restore), `Tools/UiThemeSmokeTest.cs` (registers the new report window),
`App.xaml.cs` (`SamplePhase5SelfTests` wiring + settings.json protection around the
whole self-test run)
```
Add Phase 5 polish + fix a real Recent-Files persistence bug it exposed

Loop-preview playback (LoopingSampleProvider, a small custom IWaveProvider
that repeats [LoopStart, LoopEnd) indefinitely) lets Play preview how a loop
actually sounds, independent of whether the one-shot flag is set. Delete Zone
marks a zone SKIPPEDSAMPLE rather than removing it from the RLP1 list -
removing it outright would silently expand the neighboring zones' key ranges
to fill the gap (doc's own "range runs from previous zone's TopKey+1"
convention), a surprising side effect for a delete action to have; the
underlying .KSF is left on disk, orphaned, not destroyed. Export Multisample
to Folder fills the real gap between single-sample export and whole-
collection export. The normalization report flags samples whose rate/bit
depth differs from the collection's own majority (or that are header-only) -
a quick way to spot outliers the Kronos itself never warns about.

"Drag-drop zone reordering" from the original plan sketch was dropped rather
than implemented as originally scoped: zone list order IS key-range order
(each zone's trigger range runs from the previous zone's TopKey+1 to its own
TopKey, confirmed hardware behavior) - freely reordering zones without also
rewriting their key ranges would produce an invalid/nonsensical keymap. Zone
deletion is the substitute that actually fits a "basic sample editor"'s real
needs without violating that invariant.

Real bug found by the Recent Files self-test, not a synthetic edge case:
AppSettings.SampleRecentFiles (a List<string>, same shape as the existing
RecentHosts) was silently dropped by BOTH directions of Storage.cs's
hand-rolled settings.json reader/writer - that file special-cases every
non-primitive property individually (RecentHosts, Keybinds, Macros each get
explicit manual JSON array/object handling; anything else falls through a
generic switch that only understands string/int/double/bool/enum). Adding a
new List<string> setting without ALSO adding it to both special-case lists
means it round-trips through AppSettings.Clone() (generic reflection, works
fine) but never actually persists to disk - it would have silently reset
every session. Fixed by adding the same explicit recent_hosts-shaped handling
for sample_recent_files on both the read and write side.

That same self-test run also surfaced a second, more consequential issue:
OpenCollection now writes Recent Files via Storage.SaveSettings - the REAL
app settings.json, which every Core/Sample self-test and both diagnostic
tools (SampleEditorSmokeTest, SampleEditorVisualCheck) construct a real
SampleEditorViewModel and call OpenCollection through. Every one of them was
about to start silently mutating a real user's settings.json (Recent Files
polluted with scratch temp-file paths, or worse if a future Storage bug hit
some other field) the moment they ran --librarian-selftest on their own
machine. Fixed with a snapshot/restore guard (Storage.
RunWithSettingsFileProtected for --librarian-selftest's whole run; the same
pattern inlined in the two diagnostic tools, since both call Environment.Exit
directly from multiple points and Environment.Exit does NOT run pending
try/finally blocks - the restore has to happen immediately before each exit
call, not in a finally).

All green: --librarian-selftest (incl. new SamplePhase5SelfTests),
--ui-theme-smoketest (incl. new SampleNormalizationReportWindow),
--sample-format-fixture-check (still 75/75), --sample-editor-smoketest.
Independently verified settings.json is byte-for-byte absent again after
every one of these runs (this dev environment has none persisted), not just
"tests pass" - the actual bug being fixed was a real file left behind, so
that's what got checked.
```

## `kronosology` repo (sibling repo, same feature)

**10. Hardware-confirmed loop-point / original-key fields**
Files: `docs/interfaces/ksc_kmp_ksf_file_format.md` (new file — appears untracked, not
modified, in `git status`)
```
Confirm .KSF loop points and .KMP Original/Top Key via controlled hardware test

SMP1 offsets 16/20/24/28 = Sample Start/Loop Start/[dup]/Loop End, SMD1 flags bit
0x80 = loop enable. KMP RLP1 offset 0/1 = Original Key/Top Key, correcting the
prior decompiled "key-range high/mUnknownLow" guess. Both confirmed via matched
LOOP/NOLOOP/LOOPKEY test files built on real Kronos hardware (gitignored, not
committed). RLP1 unknown4 (bytes 2-5) confirmed NOT original-key.

Also, from the C# port's fixture sweep: SMF1's real chunk position (before
SMD1, not strictly "trailing") and a 4-for-4 same-multisample cross-reference
pattern in its payload; confirms LoopEnd is a stored/preserved value, not
re-derived from frame count (a corrupted header-only .KSF keeps a stale
pre-corruption LoopEnd); and flags two data-loss/mislabeling bugs in the
Python POC (kronos_ksc_format.py) worth fixing there too if it stays in use.

New §2.2 (added same session, before implementing stereo pair creation in
KronosScreenRemote): confirms a Kronos stereo instrument is two complete
.KMP multisamples - same Name, opposite -L/-R Suffix, adjacent MNO1, matching
zone key ranges - never two zones inside one .KMP (RLP1 has no channel
field). Also confirms SMF1 plays no role in real pairs (they link purely
through Name+Suffix+MNO1), and documents a known ambiguity: the same real
fixture set that confirmed the pairing convention also contains an unrelated
unpaired mono multisample sitting MNO1-adjacent to a real pair, where a
Name+Suffix+adjacent-MNO1 heuristic alone would pick the wrong sibling - the
format has no stronger signal to disambiguate with.
```

Note: `kronosology`'s working tree also has several other modified files
(`docs/README.md`, various `reconstructed/*/README.md`, `STGGmpModule.c`, plus two
untracked `tools/` scripts) that predate this session and aren't part of this feature —
left alone, not included above.
