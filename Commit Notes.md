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
- **UI feedback pass** (after an actual click-through of the app): pane
  borders, an empty-state toolbar, note-name key fields, a click-to-select
  keymap strip, Sample Start/Loop Start-End markers directly on the waveform,
  a zoom grid + time ruler + horizontal scrollbar, distinct waveform colors
  (the trace and the old selection fill were literally the same color),
  read-only Sample Rate, a volume slider + VU meter, a right-click Cut/Copy/
  Paste/fade/gain/loop context menu, and a `_UserBank.KSC` read guard (it's a
  live shortcut to Kronos SSD content, not real sample data - hardware-
  confirmed output-only, matching the existing write-side guard).

Four real bugs were found (each by an off-hardware self-test or the
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
4. Recent Files "did nothing at all" in the running app: its MenuItem had no
   child items in XAML, so WPF never gave it a submenu arrow or fired the
   event that would populate it - same "silently does nothing" failure shape
   as bug #1, just in XAML instead of C#.

All verification suites green throughout: `--librarian-selftest` (dozens of
new self-tests across the format layer, DSP, FTP wiring, transcode, stereo,
UI-feedback logic, and this session's bug regressions), `--ui-theme-smoketest`,
`--sample-format-fixture-check` (75/75 real files, byte-identical), an
extended `--sample-editor-smoketest` E2E run against real hardware-pulled
fixtures covering the full pipeline (open → edit → DSP+undo → export/import
round-trip → stereo pair creation/import → save/reload persistence), and
`--sample-editor-visual-check` screenshots actually inspected for the whole
redesigned layout. Still wants a human click-through for: audio output of
loop preview/volume/VU, the right-click menu's live behavior, and a real
Kronos FTP pull with the `_UserBank.KSC` filter.

11 suggested commits below (10 in `KronosScreenRemote`, 1 in `kronosology`) if
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

**10. UI feedback batch — borders/toolbar, note-name keys, keymap view, waveform
markers/zoom/ruler/scrollbar, volume+VU, right-click DSP menu, read-only rate,
_UserBank.KSC guard, Recent Files fix**
Files: `Core/Sample/MidiNoteName.cs` (new), `SampleClipboard.cs` (new),
`Dsp/GainAdjustEffect.cs` (new), `SamplePhase6SelfTests.cs` (new),
`SamplePlayback.cs` (Volume, metering, unified sample-provider chain),
`ViewModels/SampleEditorViewModel.cs` (`IsUserBank` guard, `CurrentMultisampleZones`/
`SelectedZoneObject`, Cut/Copy/Paste, selection-scoped fade, gain-adjust, loop-from-
selection, `Volume`/`GetPlaybackLevel`), `Views/SampleEditorWindow.xaml`/`.xaml.cs`
(toolbar, pane borders, field sectioning, note-name key fields, read-only Sample Rate,
keymap panel, waveform ruler/scrollbar/VU/volume wiring, right-click context menu,
Recent Files submenu placeholder fix), `Views/SampleWaveformControl.cs` (rewritten -
dedicated brushes, Sample Start/Loop region markers, zoom grid, view-window API),
`Views/SampleWaveformRulerControl.cs` (new), `Views/SampleKeymapControl.cs` (new),
`Views/SampleRemoteBrowserDialog.xaml.cs` (_UserBank.KSC filtered from remote listing),
`Themes/Dark.xaml` (`WaveformTraceBrush`/`WaveformSelectionBrush`/
`WaveformLoopRegionBrush`/`WaveformGridLineBrush`),
`App.xaml.cs` (`SamplePhase6SelfTests` wiring)
```
Address a real user click-through session's worth of UI feedback

Every item here came from actually running the app and clicking through it,
not a design review - each is either a genuine usability gap or a real bug:

- Recent Files "did nothing at all": the MenuItem had zero child items in
  XAML, so WPF never gave it a submenu arrow or fired SubmenuOpened at all -
  same root cause class as this session's earlier stale-selection bug, a
  control silently doing nothing rather than failing loudly. Fixed by adding
  the same placeholder-child-item pattern MainWindow's own working Recent
  Connections menu already uses (MENU_RecentHosts).
- _UserBank.KSC is a live shortcut to Kronos SSD library content, not real
  sample data (hardware-confirmed Kronos-generated-output-only, same fact
  KscCollection.ToBytes's write-side guard already encoded) - there's nothing
  in one to actually edit. Added a read-side guard (IsUserBank) at
  OpenCollection itself, so every entry point (direct open, FTP pull, Recent
  Files) inherits it automatically, plus filtered it out of the remote
  browser's listing so it's never even selectable during a Kronos pull.
- Waveform trace and drag-selection fill were the exact same color
  (AccentBrush and SelectionHighlightBrush are deliberately identical - a
  2026-07-20 decision for the Librarian's OWN tree-row highlighting, unrelated
  to the waveform and not touched here). Gave the waveform its own dedicated
  brushes instead of reusing app-wide ones for an unrelated purpose.
- Keys shown as raw MIDI numbers - added MidiNoteName (round-tripped against
  every 0-127 value, and against this session's own hardware-confirmed C4=60
  test data) and switched every key-entry field/prompt in the editor to it.
  The underlying data model stays numeric 0-127; only entry/display changed.
- No way to see which sample owns which key range at a glance - added
  SampleKeymapControl, a click-to-select 128-key strip with one colored band
  per zone.
- Sample Start/Loop points were invisible on the waveform itself and buried
  in an undifferentiated wall of fields - added a green Sample Start marker
  line, a faint blue Loop Start/End region (shown whenever LoopEnd>LoopStart,
  regardless of the Kronos-side enable flag - "where the loop points are,"
  not "whether it's active"), and split the fields into bordered "Playback
  Format" / "Sample Start / Loop Points" sections.
- No zoom scale reference, no way to jump to a specific spot, no way to
  finely position without repeated scroll-zooming - added a vertical gridline
  overlay (nice 1/2/5×10^n frame intervals), a separate SampleWaveformRuler
  Control (time ticks, same nice-interval algorithm in seconds) kept in sync
  via the waveform's own new ViewChanged event/SetView method, and a
  horizontal ScrollBar wired through the same API.
- Sample Rate was freely retypeable despite changing it alone (without an
  actual resample) desyncing the declared rate from the real PCM - now
  read-only/informational; an actual rate change only happens via a real
  resample operation.
- No volume control and loud samples could genuinely startle - added a
  vertical volume slider (WasapiOut.Volume, unified into one unauthenticated
  ISampleProvider chain both Play and PlayLooped now share, so metering and
  volume work identically either way) and a VU meter (NAudio's own
  MeteringSampleProvider, polled by a UI timer at ~25Hz rather than pushing
  updates from the audio thread).
- No right-click menu - added one: Cut/Copy/Paste (a new in-app
  SampleClipboard - deliberately not the OS clipboard, raw PCM has no
  standard format worth interoperating with outside this app), Undo/Redo,
  selection-scoped Fade In/Out (distinct from the panel's own whole-buffer-
  edge fade), Normalize, Amplify/Soften presets (new GainAdjustEffect, a
  fixed-dB gain distinct from GainNormalizeEffect's target-peak behavior),
  and Loop Selected Area.
- Window opened to an apparent dead end with no obvious next step - added a
  toolbar (From File.../From Kronos...) mirroring the Librarian's own
  established pattern, and borders around both panes so they read as
  distinct regions instead of a borderless wall.

Verified visually (--sample-editor-visual-check, screenshots actually looked
at, not just "didn't throw") - the toolbar/borders/keymap/markers/grid/ruler/
scrollbar/VU-meter/read-only-rate/note-name-fields all render correctly
against a real hardware fixture. New SamplePhase6SelfTests.cs covers the
non-visual logic: note-name round-trip, the _UserBank.KSC guard (including
"never reaches Recent Files"), fixed-dB gain math, and Cut/Copy/Paste/
selection-fade/loop-from-selection driven through the real ViewModel with
real undo verification.

All green: --librarian-selftest (incl. new SamplePhase6SelfTests),
--ui-theme-smoketest, --sample-format-fixture-check (still 75/75),
--sample-editor-smoketest, --sample-editor-visual-check (inspected).
Right-click Cut/Copy/Paste/Fade/Amplify/Soften/Loop-Selected-Area, the actual
audio output of loop preview, and real-hardware FTP pull with the
_UserBank.KSC filter still want a human click-through - noted, not yet done.
```

**11. Second UI feedback batch — stereo L/R view, piano keymap with draggable
boundaries, loop drag/select/nudge, app-relative volume, dB-scaled VU, Play/Stop
toggle, Zero Alignment**
Files: `Core/Sample/SamplePhase7SelfTests.cs` (new), `Core/Sample/SamplePlayback.cs`
(VolumeSampleProvider instead of WasapiOut.Volume), `ViewModels/
SampleEditorViewModel.cs` (stereo partner resolution, Combine/Split mirroring across
every edit method, `MoveZoneBoundary`, `MoveLoopRegion`, `ZeroAlignmentEnabled`),
`Views/SampleEditorWindow.xaml`/`.xaml.cs` (dual waveform panes, Split L/R checkbox,
Zero Alignment checkbox, Play/Stop toggle, view/selection sync between panes),
`Views/SampleWaveformControl.cs` (loop region drag/select/arrow-nudge, `CenterOnFrame`),
`Views/SampleKeymapControl.cs` (rewritten - real piano rendering + draggable zone
boundaries), `Views/SampleVolumeControl.cs` (new), `Views/SampleVuMeterControl.cs`
(new), `Tools/SampleEditorVisualCheck.cs` (scrolled-view screenshot), `App.xaml.cs`
(`SamplePhase7SelfTests` wiring)
```
Address a second round of UI feedback: stereo view, piano keymap, loop interaction

- Stereo pairs (doc §2.2) now show as two stacked waveform panes, L always on
  top / R always on bottom regardless of which side was clicked in the tree
  (IsPrimaryLeftChannel tracks which physical pane hosts the tree-selected
  "primary" sample). A Split L/R checkbox above the keymap controls whether
  toolbar edits (Crop/Normalize/Fade/SilenceTrim/TempoPitch/GainAdjust/loop-
  from-selection/the Sample panel's own Apply/Undo/Redo) apply to BOTH
  channels using the same parameters (Combine, default) or only to whichever
  zone is selected in the tree (Split) - and whether the two panes' waveform
  selections mirror each other or stay independent. Combine-mode mirroring
  replays the exact same ISampleEffect instance against the partner's own
  PCM (a pure function of (pcm, sampleRate), trivially safe to reuse) and
  maintains a second, fully independent undo stack for the partner, undoing/
  redoing both together when they're in lockstep. Cut/Copy/Paste were
  deliberately left primary-only - a documented scope line, not an oversight.
- The keymap is now an actual piano (white/black keys, full 0-127 range) with
  a zone-assignment bar above it. Dragging the boundary BETWEEN two adjacent
  zones changes where one ends and the next begins (KmpZone.TopKey) - never
  the first zone's own low edge, which stays fixed at C-1 (there's no zone
  below it to trade keys with), with a yellow highlight and a horizontal-
  resize cursor while dragging.
- The loop region can now be dragged left/right as a whole (both LoopStart/
  LoopEnd shift together, preserving length) with a grab-hand cursor; a plain
  click (no movement) selects it (green highlight) without moving it; once
  selected, Left/Right arrow keys nudge it by one frame. A new Zero Alignment
  checkbox re-centers the waveform view on whichever loop edge just moved,
  for precise zero-crossing work.
- Volume is now a pure in-process software gain (NAudio's VolumeSampleProvider)
  instead of WasapiOut.Volume, which controls the SHARED-MODE AUDIO SESSION
  and got a visible per-app slider in Windows' own Volume Mixer plus an
  audible click-prevention ramp when changed - the reported "audio fades in"
  symptom. A software multiply has no such ramp and can't reach past this
  app's own output stage to touch anything system-level, satisfying "0..1
  relative, never affecting the system" by construction. The VU meter is
  metered AFTER the volume stage so it reflects what's actually being sent
  to the speakers, and now has a real -90..0 dB scale (SampleVuMeterControl)
  instead of a bare linear fill. The volume control itself is a custom
  centered/larger vertical control (SampleVolumeControl) with the percentage
  drawn directly inside the knob. Play and Stop are now one toggle button.

New SamplePhase7SelfTests.cs covers the non-visual logic: stereo partner
resolution from either side of a pair, Combine-mode mirroring (including a
real hardware edge case this session's own visual-check run surfaced along
the way - SampleFixtures/SMPTEST/LOOP/CLAUD001/MS001000.KSF is exactly 124
bytes, doc §3.3's header-only-corruption signature, and IS a real stereo
sibling that must be skipped, not crashed on, when mirroring edits), Split
mode NOT mirroring, MoveZoneBoundary, and MoveLoopRegion. The interactive
pieces (piano/loop mouse-drag mechanics, the volume knob's own drag, actual
audio output) are visually reviewed (rendering, colors, layout - the extended
--sample-editor-visual-check now also captures a scrolled-down view, since
the redesigned panel is taller than the window) but NOT click-tested for real
mouse-drag behavior or audio - noted below, not yet done.

All green: --librarian-selftest (incl. new SamplePhase7SelfTests),
--ui-theme-smoketest, --sample-format-fixture-check (still 75/75),
--sample-editor-smoketest, --sample-editor-visual-check (inspected against
two different real stereo pairs - one with a header-only corrupted R channel,
one with real full-length audio on both sides).

Still wants a real click-through: dragging a keymap boundary, dragging/
selecting/arrow-nudging the loop region, dragging the volume knob, actual
audio output (loop preview, the new software-gain volume control, VU meter
response), and Zero Alignment's view-centering in practice.
```

## `kronosology` repo (sibling repo, same feature)

**12. Hardware-confirmed loop-point / original-key fields**
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

---

**13. Third UI feedback batch — true stereo playback, Combine/Split semantics
inversion, marker dragging + zero-cross snapping + Loop Lock, playhead, live-editing
(no Apply buttons), Play/Stop revert bug, zoom-past-a-point blank-waveform bug, 128-zone
cap, new-zone-from-existing-sample**

Eighteen numbered items from the user, addressed as follows. Two of them changed
existing design, not just added to it:

**Combine/Split semantics inverted (items 2/3).** The previous batch's Combine mode
kept both panes visible but only rendered markers on the primary pane, and Split mode
was meant to "separate highlighting." The user corrected this: Combine (default,
unchecked) is now a single logical stereo view — both panes always visible, L top / R
bottom, sharing one selection and one set of Sample Start/Loop Start/Loop End markers;
dragging either pane edits the shared pair. Split (checked) now shows only ONE pane —
whichever channel is selected in the tree — collapsing the other entirely, rather than
showing both with independent selections. `PartnerSelectionStartFrame/EndFrame` (the old
independent-partner-selection state) were deleted as dead code under the new model.

**"Zero Alignment" replaced by "Use Zero" (item 12).** The old checkbox re-centered the
waveform VIEW on the nearer loop edge after a move — a navigation aid. The new "Use
Zero" is an editing constraint: every Sample Start/Loop Start/Loop End edit (drag or
typed) snaps to the nearest zero-crossing in the waveform (where consecutive samples
straddle the center line), found via `SampleEditorViewModel.NearestZeroCrossing` — a
bounded outward search in both directions simultaneously (closest crossing wins, not
first-found), which terminates and falls back to the original frame, unchanged, if the
signal never actually crosses zero (a DC-offset buffer) rather than looping forever.
`ZeroAlignmentEnabled`/`SampleWaveformControl.CenterOnFrame`/the window's
`CenterOnNearestLoopEdge` were all deleted, not layered under the new feature.

**The marker choke point (items 8, 9, 10, 12, 13).** Sample Start, Loop Start, and Loop
End can now be set five ways — three text fields (apply on LostFocus, no more Apply
buttons), and dragging the marker's own colored line in the waveform (new
`SampleWaveformControl.MarkerDragged` event, hit-tested the same ±5px way the existing
loop-region-body drag and keymap boundary drag already were). Every one of those five
paths, plus the Combine-mode stereo mirror, routes through one method,
`SampleEditorViewModel.SetMarker`, in a fixed order: clamp to the buffer → snap to the
nearest zero-crossing (if Use Zero) → apply Loop Lock's length preservation (computed
from the *already-snapped* edge, so the edge you actually dragged lands exactly there;
the linked edge is derived from it and may itself land a few frames off a crossing — an
accepted trade-off, documented at the call site, not a bug) → commit through
`ApplySampleFieldsTo`, which now centrally enforces "Loop Start can never precede Sample
Start" (floors Loop Start at Sample Start, then Loop End at Loop Start) for every caller
— the bulk field-apply, `SetLoopFromSelection`, `MoveLoopRegion`, and `SetMarker` all
share this one clamp, so the invariant can't be bypassed by editing one path and not
another. Whole-loop-region dragging (moving both edges together) and Loop Lock/Use Zero
edits both still go through this same central clamp; whole-region drag intentionally
does NOT get zero-cross snapping (it's a coarse reposition, not a precision edit) — a
scope decision, not an oversight.

**Kronos's own marker colors (item 11).** New `WaveformSampleStartBrush` (red,
`#FFFF0000`), `WaveformLoopStartBrush` (green, `#FF00FF00`), `WaveformLoopEndBrush`
(blue, `#FF0000FF`) in `Themes/Dark.xaml`, replacing the old shared `SuccessBrush` for
Sample Start. The loop region's translucent fill is unchanged (still the faint-blue/
green-when-selected pair from the previous batch) — these three new brushes are for the
marker LINES specifically, drawn on top of it.

**True stereo playback (item 1).** `SamplePlayback` gained `PlayStereo`/
`PlayStereoLooped`, interleaving the resolved L/R pair (`Interleave` pads whichever
channel is shorter with silence rather than truncating the longer one) into a 2-channel
`OneShotSampleWaveProvider`/`LoopingSampleProvider`. `PlaySelectedSample` now plays true
stereo whenever a stereo partner is resolved, Combine mode is active, AND the partner
actually has audio (`IsHeaderOnly` gets its fourth consumer here — falls back to mono
rather than interleaving zeros against a corrupted partner); Split mode still plays only
the tree-selected channel, matching what's on screen.

**Loop preview now matches how a hardware sampler actually plays a loop, plus
reverse-loop preview (items 6, 14, 15).** `LoopingSampleProvider` was rewritten: it now
plays `sampleStartFrame -> loopEndFrame` once (the "intro"/attack, forward, always —
`reverse` never affects the intro, only the loop itself), THEN repeats
`[loopStartFrame, loopEndFrame)` indefinitely — forward as before, or backward
(`loopEnd-1` down to `loopStart`, one direction, not a ping-pong) when the new
"Reverse Loop" checkbox (`LoopReverseEnabled`) is on. Both mono and the new stereo path
share this provider. **Reverse Loop is playback-preview only right now, NOT persisted to
the `.KSF`** — per the format spec's own Phase 0 rule (no speculative field shipped as
fact), because no real Kronos byte for "this loop plays backward" has been
hardware-confirmed. **This needs the same controlled-fixture treatment Phase 0 used for
loop points**: two samples, identical in every other respect, saved from a real Kronos
with Reverse off vs. on, byte-diffed the same way `LOOP`/`NOLOOP`/`LOOPKEY` were —
flagged back to the user, not guessed at.

**Playhead (item 6).** New `SampleWaveformControl.PlayheadFrame` (a thin white line),
driven by `SamplePlayback.PositionFrame` — each provider (`OneShotSampleWaveProvider`,
`LoopingSampleProvider`) tracks its own volatile frame counter (byte offset for one-shot,
the actual wrapping/reversing position for looped), polled by the same ~25/s timer that
already drove the VU meter — same "poll, don't marshal an event" discipline, no new
threading risk.

**Two real bugs, root-caused and fixed (items 4, 5), not redesigned around:**
- *Play/Stop never reverted to Play on its own* — `IsPlaying` flipping to `false` when
  playback finished naturally (not via clicking Stop) never triggered a UI refresh,
  because `RefreshDetailPanels` was only ever called from explicit user-action handlers,
  never in response to the ViewModel's own property changes. Fixed with one
  `_vm.PropertyChanged` subscription in the window's constructor.
- *Waveform disappears past a certain zoom level* — two compounding causes. (a) The
  trace's bucket-to-pixel mapping computed `bucketCount` from the control's pixel width
  but then broke out of the render loop after only `viewLen` iterations once zoomed in
  past ~1 frame/pixel, squeezing the whole trace into a sliver at the left edge instead
  of spanning the control. Fixed by rewriting the trace loop to iterate one bucket PER
  PIXEL COLUMN using the exact same `viewStart`/`viewLen`/width arithmetic
  `FrameToPixel` uses, so the two can't drift apart. (b) The zoom floor was a fixed 32
  frames regardless of control width, letting a wide control zoom in far past 1
  frame/pixel — the point past which more zoom shows no more detail and is what
  triggered (a). Fixed by flooring `OnMouseWheel`'s zoom at `max(1, ActualWidth)` frames
  — "zoom stops at the point it can no longer render," per the request, not an arbitrary
  number.

**128-zone cap (item 17) + new zone from an existing sample (item 18).**
`SampleImportBuilder.MaxZonesPerMultisample = 128`, enforced once inside
`AddSampleZone` (the single entry point `AddStereoSampleZonePair` also funnels through,
called twice) — throws `InvalidOperationException` rather than silently truncating or
overwriting. New `SampleEditorViewModel.AddZoneFromExistingKsf` + File menu "New Zone
from Existing Sample (.KSF)..." — reads an existing `.KSF`'s audio and duplicates it
into a new zone at a chosen key range in the current multisample, going through the same
capped `AddSampleZone` path Import Audio uses. Scope note: this covers "assign an
already-existing sample to a new zone," not a from-scratch silent/blank zone — no
existing precedent in this codebase for a zone with no audio at all.

**Keymap height (item 7).** Bumped 60px → 78px (+30%). The black-key-shorter-than-white
rendering was already correct in the code (60% height) from the previous batch; only the
control's own height needed the requested increase.

**Two regressions caught by a pre-completion review (of this same batch) and fixed
before it was called done:**
- `MoveLoopRegion` (whole-region drag) started passing its raw start/end straight
  through the new centralized ordering clamp, which only floors `loopStart` upward -
  dragging the region so its left edge would land before Sample Start left `loopEnd`
  behind, silently *shrinking* the loop toward zero length instead of the whole block
  stopping at the wall. Fixed by recomputing `loopEnd` from the (possibly clamped)
  `loopStart` + the drag's ORIGINAL length, so it's a block move, not an edge clamp.
  Locked in by `move-loop-region-stops-at-sample-start-wall`/`-preserves-length-at-wall`.
- `LoopSelected` (renders the loop region green once click-selected) didn't clear when
  starting a marker-edge drag, so dragging an edge while the region was still
  green-selected from an earlier click left arrow-key nudging still moving the WHOLE
  region afterward. Fixed by clearing it in all three marker-drag branches, matching
  what the crop-selection branch already did.

Verification this round: build clean (4 pre-existing unrelated `CS0067` warnings only);
`--librarian-selftest` green, including new `SamplePhase8SelfTests.cs` (loop-intro/
reverse frame sequencing on both mono and stereo-interleaved buffers - specifically
checking L/R stay paired frame-by-frame through a reverse loop, not swapped or
split - stereo `OneShotSampleWaveProvider` interleaving, the 128-zone cap, the marker
choke point's ordering invariant/Loop Lock/Use-Zero-snap/no-crossing-fallback, and
`MoveLoopRegion`'s wall-stop-preserves-length behavior, all via a real
`SampleEditorViewModel` against synthetic collections); `--ui-theme-smoketest` green;
`--sample-format-fixture-check
SampleFixtures` still 75/75 byte-identical (no format-layer regression); `--sample-
editor-smoketest` green end-to-end against a real hardware fixture; `--sample-editor-
visual-check` against a real two-channel stereo pair
(`ANDRE_K2_73/samplesfeb28_25.KSC`) confirms the Combine-mode fix directly — both L and R
panes render with the SAME loop-region fill now, not just the primary pane — plus the
taller keymap, Use Zero/Loop Lock/Reverse Loop checkboxes, and the bottom-right Save
Changes button with no remaining Apply buttons.

**Still needs a real click-through** (can't be verified by a static screenshot or a
headless self-test): dragging a marker line (Sample Start/Loop Start/Loop End) in the
waveform, dragging a keymap boundary, dragging/selecting/arrow-nudging the loop region,
dragging the volume knob, actual audio output (stereo playback, loop preview including
reverse, the playhead's real-time position, VU meter response), and Use Zero's snapping
behavior against real audio (the self-tests use small synthetic buffers with hand-placed
crossings, not a real waveform).

**Not implemented — needs the user's hardware fixture pair first (item 16):** Reverse
Loop is preview-only, not persisted. Same rule Phase 0 used for loop points: no
speculative byte shipped as fact.

---

**14. Third UI feedback batch's follow-up round — loop-region gating fixes the
click/drag/toggle bugs, equal-height stereo panes with a divider, a correct
white-key-proportional piano, Add Zone, Save Changes padding, dual-channel Use Zero,
undo now covers field/marker edits (not just PCM), stereo-shared Normalize**

Ten numbered items. Two required tracing through the interaction/undo logic by hand to
find real bugs before they were fixable - both caught and fixed before reporting done.

**Item 1 (waveform click/drag/highlight) - root cause was the loop region swallowing
almost every click.** A sample's loop defaults to spanning [0, frameCount) - with no
gate, `SampleWaveformControl.InLoopRegion` treated nearly every click as "inside the
loop," which made plain crop-selection (drag to select) impossible and the loop's own
green "selected" highlight impossible to clear (nowhere outside the loop to click to
clear it). Fixed with a new `LoopEnabled` property on the control, wired from the
sample's own Loop Enabled checkbox: ALL loop-region interactivity (fill, green/blue edge
lines, click-to-select, whole-region drag, independent edge drag, arrow-key nudge) is
now gated on it - unchecked, the waveform is pure crop-selection only, exactly like the
user described wanting. A **self-review before reporting this fixed** caught that the
click-to-select toggle was still one-directional (`LoopSelected = true` always, never
back to `false`) - even with the gate, a looped sample would still get stuck green after
one click, since the only way out was clicking outside the loop, which is what the user
explicitly said was the problem. Fixed to `LoopSelected = !LoopSelected`, an actual
toggle now. Also fixed: `LoopSelected` was private per-control state with NO
synchronization between the L and R panes in Combine mode - clicking to select the loop
on one pane never showed green on the other. Made it a real `DependencyProperty` with a
`LoopSelectedChanged` event; the window mirrors it onto the sibling pane the same way
markers/selection already mirror.

**Item 2 (unequal pane heights, no divider).** `WaveformLeft` was hardcoded to 200px,
`WaveformRight` to 140px - a straightforward inconsistency, not a logic bug. Both are now
170px, with a new 2px `WaveformDivider` bar between them (visible only when the R pane
is).

**Item 3 (piano still wrong).** The previous round's piano treated all 128 MIDI notes as
equal-width slots on a uniform chromatic grid - visually a "comb," not a piano (black
keys evenly spaced under every semitone instead of clustered in real 2-and-3 groups).
Rewritten to be white-key-proportional: white keys get equal-width slots sized from the
TOTAL WHITE KEY COUNT (not 128), and each black key is centered on the boundary between
the two white keys it visually falls between - the standard simplified-piano-strip
layout. The zone-assignment bar and boundary-drag hit-testing were switched to the same
coordinate system so they stay aligned with the keys below them. Confirmed visually -
now shows the correct 2-and-3 black-key octave grouping.

**Item 4 (no Add Zone).** The previous round added a "New Zone from Existing Sample"
File-menu item, but that's not where the user was looking - they were looking right next
to Delete Zone. Added an "Add Zone..." button there, wired to the same
`AddZoneFromExistingKsf` flow (pick a `.KSF`, pick a key range).

**Item 5 (Save Changes padding).** Bumped the button's own padding (12→14/3→4) and gave
it real margin on both sides (10px from the status text, 4px + the bar's own 6px = 10px
from the window edge) - previously it had zero right margin and sat flush against the
Border's own padding only.

**Item 6 (Use Zero not snapping) - two real fixes, one honest caveat.** (a) Use Zero now
searches BOTH channels of a resolved stereo pair, not just the primary - either
channel's crossing is a valid snap target, nearer one wins, an exact tie picks the lower
frame (explicit instruction). (b) Tracing through the dual-channel comparison surfaced a
real bug before it shipped: `NearestZeroCrossing` returning the unchanged input frame
for BOTH "no crossing exists" and "found one exactly at the target" made a channel with
NO crossing at all falsely win every distance comparison (distance 0) against a real
crossing on the other channel, no matter how close. Fixed by returning `int?` (null =
genuinely not found) instead of silently falling back to the raw frame; a self-test
(`use-zero-finds-crossing-on-partner-channel`) specifically catches this by construction
(primary has literally no crossing anywhere, partner has one 2 frames away - must find
the partner's). **Caveat, not fixed further this round:** real audio at 44.1kHz often
has zero-crossings every few dozen frames or less: at typical full-waveform zoom (a
200,000-frame sample across ~1000px is ~200 frames/pixel), a snap of a few dozen frames
can be sub-pixel and genuinely invisible on screen even though it's working correctly.
Zooming in (mouse wheel) before placing a marker should make the snap visible.

**Item 7 (stop-glitch) - reduced, not eliminated.** WASAPI's own output buffer (was
100ms, now 40ms - matched to the VU timer's own polling interval) isn't flushed by
`Stop()`; whatever's already queued keeps playing out for up to the configured latency
after Stop is called, worse for a looping source since the queued tail can span a loop
wrap. Shrinking the buffer shrinks the glitch; it doesn't remove it - WasapiOut has no
flush primitive. If the residual tail is still audible, the next step would be having
the provider itself go silent on Stop (return zeros rather than relying on the device
to actually stop) - not built, since it wasn't asked for.

**Item 8 (bottom-justify).** `FieldLabel`'s style and the `FieldBox`/`KeyFieldBox`/
`ReadOnlyFieldBox` styles all switched from `VerticalAlignment="Center"` to `"Bottom"`,
plus every Button/CheckBox sharing a WrapPanel row with them got an explicit
`VerticalAlignment="Bottom"` (WrapPanel doesn't stretch children to a shared height the
way Grid does - each item needs its own alignment set to land on a shared baseline).

**Item 9 (Ctrl+Z/Y "not bound") - the key handling was already correct; the real bug was
that most edits were never being recorded to undo at all.** `OnWindowPreviewKeyDown`'s
Ctrl+Z/Ctrl+Y handling (tunnels from the Window before any focused TextBox's own
handling) was already present and correct. But `SetMarker`, `SetLoopEnabled`,
`MoveLoopRegion`, `SetLoopFromSelection`, and the bulk `ApplySampleEdits` - the entire
live-editing workflow this round's "no Apply buttons" change made the PRIMARY way to
edit a sample - never called `RecordBeforeEdit` at all. Ctrl+Z after typing a Loop Start
value did nothing observable, not because the shortcut wasn't wired, but because there
was nothing on the undo stack to undo - which reads exactly like "it's not bound." Fixed
properly, not worked around: `SampleEditUndo` was redesigned around a new
`SampleFieldSnapshot` (Pcm + SampleStart/LoopStart/LoopEnd/Flags together) instead of a
bare `byte[]` - bundling PCM and field state into ONE snapshot type, on ONE shared stack,
makes chronological ordering between PCM edits (crop/normalize/fade/gain/tempo) and
field edits (marker drags, typed fields, Loop Enabled) correct BY CONSTRUCTION: whichever
kind of edit happened most recently is simply the top of the one stack, no merge/
interleave logic needed. All five field-editing methods now call `RecordBeforeEdit`/
`RefreshUndoRedoState` the same way the PCM-editing methods always did. Locked in with
two new self-tests: a field-only edit (`SetMarker`) now shows `CanUndo == true` and a
genuine revert on `Undo()`, and a PCM-edit-then-field-edit sequence undoes in the correct
order (field edit first, then the PCM edit) through the single shared stack. **Known,
unaddressed gap**: zone key-range edits (Original Key/Top Key, on `KmpZone`, not
`KsfSample`) are still NOT covered by Undo/Redo - out of scope for this fix (a different
object type, would need its own mechanism), not silently forgotten.

**Item 10 (Normalize per-channel).** Confirmed real: `GainNormalizeEffect.Apply` measures
its own peak from whatever buffer it's given, and the stereo-mirroring path called it
separately per channel - each channel got its OWN scale factor from its OWN peak,
independently, silently shifting the stereo balance (a quieter channel gets boosted more
than its louder partner). Fixed with a `sharedPeak` override on the effect: when
mirroring, `ApplyNormalize` measures both channels' peaks up front, uses the LOUDER one,
and bakes that single shared peak into one effect instance reused for both channels - so
both scale by the same factor and preserve their relative balance. Split mode (no
mirroring) still normalizes independently, correctly, since there's no "pair" to keep
proportional in that mode.

Verification: build clean (same 4 pre-existing unrelated warnings only); `--librarian-
selftest` green, including a new `SamplePhase9SelfTests.cs` (dual-channel Use Zero with
the null-vs-found-at-target distinction, the tie-break-picks-lower-frame case, stereo
shared-peak Normalize via a proportionality assertion, field-only-edit undo, and
chronological PCM-then-field undo ordering) plus new checks appended to the existing
undo self-test for the `SampleFieldSnapshot` API change; `--ui-theme-smoketest` green;
`--sample-format-fixture-check SampleFixtures` still 75/75 byte-identical; `--sample-
editor-smoketest` green end-to-end; `--sample-editor-visual-check` against the same real
stereo pair confirms equal pane heights + divider, the corrected piano, Add Zone next to
Delete Zone, Save Changes' padding, and Loop Enabled unchecked correctly hiding all loop
UI (no blue fill dominating the waveform, "No selection - drag to select" message
showing instead).

**Still needs a real click-through**: the loop-region toggle-on-click fix specifically
(click once = green, click again = clear); dragging a marker/keymap boundary/loop edge;
Ctrl+Z/Ctrl+Y after a marker edit (self-tested at the VM level, not yet exercised through
the actual keyboard handler + a live window); the stop-glitch's actual audibility after
the latency reduction; and whether Use Zero's snap is visible at the zoom level the user
was testing at (see item 6's caveat above - may need to zoom in to see it).

**Follow-up fix, same round, caught in review before this entry was closed out**: the
`SampleFieldSnapshot` redesign above captured four of the five fields `ApplyTo` actually
needs to restore. `KsfSample._preservedLoopDuplicate` (the offset-24 slot documented in
entry 12 - mirrors LoopStart on 73/75 real files, but holds a genuinely distinct value on
5 real outliers) wasn't part of the snapshot at all, and `ApplyTo` unconditionally called
`ClearPreservedLoopDuplicate()` on every restore. That's silent data loss specifically on
those 5 outlier files: edit a field, undo it, and the dup slot - which should have gone
back to its original distinct value - instead gets nulled, so the next Save mirrors
LoopStart into offset 24 and changes bytes that round-tripped byte-identical before any
edit ever touched the file. The 75/75 fixture-check couldn't catch this - it's an
Open-then-ToBytes check with no edit/undo in between, so `ApplyTo` never runs in that
path. Fixed by exposing `KsfSample.PreservedLoopDuplicate` (getter) and
`RestorePreservedLoopDuplicate(uint?)` (setter distinct from the existing
`ClearPreservedLoopDuplicate()`, which is still used by the live-edit path itself),
adding a fifth optional field to `SampleFieldSnapshot`, and having `ApplyTo` call
`RestorePreservedLoopDuplicate(PreservedLoopDuplicate)` instead of unconditionally
clearing. New self-test (`SampleDspSelfTests.cs`, "dup-undo-restores-preserved-
duplicate") constructs a sample with a distinct dup value, edits LoopStart the same way
`SetMarker` does (mutate + `ClearPreservedLoopDuplicate()`), undoes it, and asserts the
original dup value comes back rather than null. Also fixed in the same pass: `Loop
Selected` (the green highlight DP) was sticky across `Loop Enabled` being unchecked -
`HasLoop` already hid all loop UI while disabled, but re-checking the box made the green
highlight reappear with no click, since the toggle in `OnMouseLeftButtonUp` never ran
while it was hidden. `RefreshDetailPanels` now clears `LoopSelected` on the pane(s)
whenever `SampleLoopEnabled` is false. Verification: build clean (same 4 warnings),
`--librarian-selftest` green including the new dup-preservation check.

**15. Fourth UI feedback batch, Pass 1 - the diagnosed/near-diagnosed fixes (zoom
persisting across a loop drag, Sample Start's render bug at frame 0, Play actually
looping, click-drag stereo mirroring live, Ctrl+A, stereo-shared Trim Silence, Split L/R
updating immediately, the keymap boundary drag's pixel mapping, and a full undo/redo
foundation for zone-list edits).** The user reported 13 new issues after the previous
round's click-through. Four were genuinely new subsystems (transport controls, empty
placeholder zones, drag-reorder zones, and the undo half of the keymap fix) deferred to
a second pass; the rest were diagnosed and fixed here:

- **Zoom resets on every loop-region drag.** Root cause: `KsfSample.Samples()` decodes a
  brand-new `short[]` from the underlying bytes on EVERY call, so any field-only edit
  (a marker drag, a loop-region move, toggling Loop Enabled) that re-reads and reassigns
  `SampleWaveformControl.Samples` produces a new array REFERENCE even though the PCM
  content/length is unchanged - and the old `OnSamplesChanged` unconditionally reset
  pan/zoom to "show everything" on any such reassignment. Fixed: only reset when the
  frame COUNT actually changes (a genuinely different sample, or a length-changing edit
  like crop) - same reference-vs-content distinction the `SampleFieldSnapshot` bug from
  the previous entry turned on.
- **Sample Start invisible at frame 0.** The render condition was `SampleStartFrame >
  viewStart` (strict), which excludes the exact case of the marker sitting at the view's
  own left edge - frame 0 is exactly that case on first load. Same off-by-one existed on
  the loop-start/loop-end edge lines; all three now use `>=`. Confirmed in the visual
  check screenshot (red line now visible at the waveform's left edge).
- **"Minimum drag position is 7, not 0."** Traced the full clamp chain in `SetMarker`/
  `ApplySampleFieldsTo` - there is no floor anywhere in it when Use Zero is off. Added a
  diagnostic self-test (`usezero-off-lands-exactly-on-zero`/`-on-target`) proving
  `SetMarker(SampleStart, 0)` lands exactly on 0 with Use Zero off. `UseZeroCrossing` is
  a plain session-sticky VM property never reset by `LoadSampleDetailState` - almost
  certainly still checked from testing the previous round's item 6, snapping every low
  value to the same nearby crossing. Not a new bug; flagged for the user to confirm by
  checking the box's actual state.
- **Loop Enabled checked, but Play doesn't loop.** `PlaySelectedSample` gated looped
  playback on `LoopPreviewEnabled` alone - a SEPARATE UI-only checkbox from `Loop
  Enabled`/`SampleLoopEnabled` (the real Kronos flag). Checking "Loop Enabled" itself did
  nothing to playback. Fixed: loops whenever EITHER is true - `LoopPreviewEnabled` still
  lets loop points be previewed before committing the flag, but the flag alone now also
  loops, matching what checking it actually implies.
- **Click-drag highlighting only one side at a time (stereo).** The stereo mirror only
  happened once, on mouse-up (`SelectionChanged`) - during the drag itself only the pane
  under the cursor updated. Added `SampleWaveformControl.SelectionPreviewChanged`,
  firing on every intermediate `MouseMove` of a plain crop-selection drag; the window
  mirrors the live `SelectionStartFrame`/`EndFrame` directly onto the sibling pane (a
  cheap DP-to-DP copy, deliberately not routed through the ViewModel/`RefreshDetailPanels`
  on every pixel of movement).
- **Ctrl+A doesn't select all.** Wasn't wired at all. Added to
  `SampleWaveformControl.OnKeyDown`: selects `[0, FrameCount)` and fires
  `SelectionChanged`.
- **Trim Silence trims each stereo channel independently, offsetting the pair.**
  `SilenceTrimEffect` computed its own leading/trailing bounds per buffer, and
  `ApplyEffect` replays the same instance against both channels - which is exactly why
  they diverged when the two channels' silence runs differed in length. Fixed the same
  way `GainNormalizeEffect`'s shared peak works: `SilenceTrimEffect` gained an optional
  `sharedBounds` param and a static `ComputeBounds` helper; `ApplySilenceTrim` computes
  each channel's own bounds up front and passes in their UNION (`min` of the two starts,
  `max` of the two ends) as the shared bounds, so both channels crop to the identical
  range - "only delete silence present in BOTH channels," per the user's own framing.
- **Split L/R doesn't update until some other action refreshes it.** `OnSplitLRChanged`
  set `_vm.SplitLR` but never called `RefreshDetailPanels()` - the pane layout only
  caught up whenever an unrelated click/selection happened to trigger a refresh. One
  missing call.
- **Play/Stop button colors.** `BtnPlayStop.Foreground` now switches between the existing
  `SuccessBrush` (green, Play) and `DangerTextBrush` (red, Stop) - both already defined
  in `Themes/Dark.xaml` for other buttons, reused rather than inventing new colors.
- **Keymap boundary drag: broken in two ways, both traced to the same bug.** The drag
  handler computed the proposed key as `(int)(x / whiteWidth)` - a WHITE-KEY INDEX (0..75
  white keys) - then clamped/compared it directly against real MIDI key numbers (0..127).
  The piano layout isn't linear in MIDI number (black keys interspersed at 60% width), so
  a white-key index diverges further from the true MIDI number the higher up the keyboard
  you go - by the top of the range a white-key index is barely half the real MIDI number.
  That's why the yellow line didn't track the cursor correctly and dragging seemed to
  hit an invisible wall well short of the right edge ("doesn't allow drag in the opposite
  direction"). Fixed with `PixelToBoundaryKey`, a nearest-boundary-position scan over the
  ACTUAL candidate pixel positions (the same coordinate space `BoundaryX`/rendering use),
  not a linear divide.
- **Keymap boundary drag: no undo at all.** `KmpZone` edits were never covered by any
  undo mechanism (a known, previously-documented gap - see entry 14's undo redesign,
  which was scoped to `KsfSample` PCM/fields only). Added a proper foundation rather than
  a one-off patch, since Add Zone and drag-reorder (both deferred to Pass 2) will need
  the same thing:
  - `ZoneListSnapshot` (`Core/Sample/SampleZoneUndo.cs`) - a deep-cloned snapshot of one
    multisample's full zone list. `ApplyTo` restores by MUTATING THE EXISTING `KmpZone`
    INSTANCES in place (matched by index) rather than swapping in new objects - the tree,
    `_selectedZone`, and the keymap's `SelectedZone` binding all hold direct references
    to these exact instances, and replacing them would silently orphan those references
    from what's actually back in the list (caught by a self-test that captured a zone
    reference before the edit and asserted it - not a freshly-looked-up one - reflected
    the reverted value).
  - `SampleZoneUndo` - a plain step-capped stack (zone lists are small; no byte-cap
    machinery needed, unlike PCM).
  - Zone-list undo is a SEPARATE stack from `_sampleUndo`/`_partnerUndo` (different
    object graph: a multisample's zone list vs. one `KsfSample`'s PCM/fields). Rather
    than force them into one snapshot type, `SampleEditorViewModel` now keeps
    `_undoDomains`/`_redoDomains` (`List<EditDomain>`, used as stacks) recording, in
    order, WHICH underlying stack each logical edit belongs to - so `Undo()`/`Redo()`
    pop the right stack and walk back through a MIXED history of sample and zone edits
    in the actual order they happened. Every existing `_sampleUndo.RecordBeforeEdit`
    call site now also pushes `EditDomain.Sample`; `MoveZoneBoundary` pushes
    `EditDomain.Zone`.
  - Zone-undo scope is the MULTISAMPLE, not the zone selection - clicking between zones
    within the same multisample (a completely normal thing to do right after a boundary
    drag, to check the result) must not wipe that drag's undo entry before Ctrl+Z gets a
    chance to run. `SelectNode` only resets `_zoneUndo`/drops `EditDomain.Zone` entries
    when `CurrentMultisampleZones` is about to point at a genuinely different list;
    `EditDomain.Sample` entries are dropped unconditionally, since `_sampleUndo` itself
    always resets on every `SelectNode` (pre-existing behavior, unchanged).
  - A real bug caught and fixed while writing the self-test for this (before reporting
    done): the first `ApplyTo` implementation cleared the list and re-added freshly
    cloned zones wholesale - `zone-undo-reverts` failed because the test's captured zone
    reference no longer matched anything in the list after undo. Rewritten to copy
    fields into the existing instances instead (see `ZoneListSnapshot.ApplyTo` above).
  - `RefreshDetailPanels` now also calls `Keymap.InvalidateVisual()` unconditionally -
    since undo/redo mutates the zone list's existing objects in place rather than
    reassigning the `Zones` DP to a new list reference, WPF's own change detection never
    fires on its own after an undo/redo.

New tests: `Core/Sample/SamplePhase10SelfTests.cs` (Use Zero floor diagnostic, zone-undo
apply/revert/redo, zone-undo survives same-multisample navigation, cross-domain
chronological ordering between a sample edit and a zone edit, stereo-shared silence
trim). Verification: build clean (same 4 pre-existing warnings), `--librarian-selftest`
green (including the 11 new checks), `--ui-theme-smoketest` green, `--sample-editor-
smoketest` green end-to-end, `--sample-format-fixture-check SampleFixtures` still 75/75
byte-identical (no zone/field save-path touched this pass), `--sample-editor-visual-check`
against a real stereo pair confirms the Sample Start marker now renders at frame 0.

**Deferred to Pass 2** (new subsystems, not diagnosed fixes - the user said "address the
above before proceeding," not "in one pass"): a transport control bar (rewind-to-start/
rewind/play-stop/pause/fast-forward/go-to-end, icons matched to the main app's
sequencer transport) replacing the single Play/Stop button; a grey scrub-line + click-to-
play-from-position gesture; Add Zone creating an empty placeholder (no `.KSF` yet) rather
than only reusing the existing "import a KSF into a new zone" flow; and keymap zone
drag-reorder (with the "keymap size stays the same, top key becomes what the replaced
zone's used to be" rule) - which will reuse the `SampleZoneUndo` foundation built this
pass rather than needing its own.

**16. Fourth UI feedback batch, Pass 2 - the four deferred new subsystems: transport
bar, scrub-click-to-play, Add Zone placeholders, keymap zone drag-reorder.** Two
clarifying questions were needed before starting - the user's answers are recorded
below since they're load-bearing design decisions, not just implementation notes.

- **Transport bar** (`Views/SampleEditorWindow.xaml`): the single Play/Stop button
  replaced with |◀ ◀◀ ▶/■ ❙❙ ▶▶ ▶| - a new `SampleTransportBtn` style and Path
  geometry copied VERBATIM from `MainWindow.xaml`'s `SeqTransportBtn`/`BTN_SeqLocate`/
  `BTN_SeqRew`/`BTN_SeqFf`/`BTN_SeqPause`/`BTN_SeqStart` (the main app's own sequencer
  transport row), not redrawn from scratch - "familiar," per the ask, means literally
  the same icons, not a lookalike. Play/Stop's triangle/square recolored green/red per
  entry 15's item 4 (the main app's own version is monochrome; the user's explicit
  color ask overrides matching it exactly here).
  - New `SampleEditorViewModel` transport surface: `TransportLocateStart`/`
    TransportLocateEnd` (jump to frame 0 / last frame), `TransportSeekRelative(±1)`
    (steps by 10% of the sample's frame count, floored at 1 frame - scales with sample
    length rather than a fixed constant), `TransportTogglePause`/`IsPaused`. Locate/
    Rewind/FF only start audio if playback was ALREADY playing or paused - pressed
    while fully stopped, they just relocate the grey scrub-line cursor (a new
    `CursorMoved` event) without making a sound, matching how a real transport's Locate
    behaves (repositions, doesn't force playback from nothing).
  - Pause is a deliberate simplification, stated as such rather than silently dropped:
    resuming plays ONE-SHOT from the paused frame to the end, not a resumed loop - if
    the sample was looping when paused, Resume does not re-enter the loop. Same
    trade-off `PlayFromFrame`/scrub-click already makes (entry 15's item 6's sibling,
    item 5 below) - "basic controls," per the user's own framing, not a full transport
    engine.
  - `SamplePlayback`/`OneShotSampleWaveProvider` gained a `startFrame` parameter
    (`PlayFrom`/`PlayStereoFrom`) - sets the provider's internal byte cursor directly at
    construction rather than always starting at 0, reused by both the transport bar and
    item 5's scrub-click below.
- **Item 5 - scrub-click "play from here."** A plain click (no drag) anywhere on the
  waveform, outside any marker/loop-region hit, now sets a NEW grey `ScrubFrame` line
  (`SampleWaveformControl`) and fires `ScrubRequested`, which the window turns into
  `PlayFromFrame` (one-shot from that point, ignoring loop state - an audition gesture,
  not a statement about normal playback). Distinguishing a plain click from a drag
  needed the same "did the mouse actually move" tracking the loop-region drag already
  uses (`_dragMoved`, mirroring `_loopDragMoved`'s existing pattern) - on a NON-moved
  click, the pre-click selection is restored (a scrub-click previews a spot, it
  doesn't blow away an existing crop selection) instead of leaving the momentarily-
  collapsed zero-width selection Mouse-down always sets.
- **Item 11 - Add Zone as an empty placeholder.** Clarified with the user up front
  (question 1): reuses the existing SKIPPEDSAMPLE convention (`Filename` = the doc's
  own "no real .KSF backs this" marker) rather than inventing a new zone state every
  consumer (tree/keymap/save) would need to learn. `SampleEditorViewModel.
  AddPlaceholderZone` appends the new zone at the END of the keymap, claiming up to one
  octave off the top of whatever the current last zone owns (half its range if
  narrower than that) rather than disturbing any OTHER zone. A new "Import Sample..."
  button/`ImportSampleIntoZone` attaches real audio to an existing zone afterward -
  replaces `Filename` with a freshly generated one and writes the `.KSF`, WITHOUT
  touching the zone's own key range (`OriginalKey`/`TopKey` untouched - "give this slot
  audio," not "add a new slot"). Neither method is wired into zone-list undo (Ctrl+Z) -
  consistent with every other zone-ADDING method in this file (`ImportAudioAsNewZone`,
  `AddZoneFromExistingKsf`, the stereo-pair builders): they all go through a full
  Save + tree rebuild from disk, which replaces every `KmpZone` instance and is
  incompatible with `_zoneUndo`'s live-object-identity design (see entry 15's zone-
  undo writeup) - Delete Zone remains the manual undo for an accidental Add, same as
  today.
- **Item 12 - keymap zone drag-reorder.** Clarified with the user (question 2, after
  an initial guess - "swap" - turned out wrong): dragging zone B onto zone A's slot
  means each zone keeps its OWN key-range WIDTH, only its POSITION in the sequence
  changes - a 10-wide zone dragged in front of a 20-wide zone stays 10-wide in its new
  (earlier) slot, the 20-wide zone stays 20-wide in its new (later) slot. Implemented
  as `SampleEditorViewModel.ReorderZone`: captures every zone's width (`TopKey` minus
  the previous zone's `TopKey`) BEFORE the move, removes/re-inserts the dragged zone at
  the target's list position, then recomputes every zone's `TopKey` as a running
  cumulative sum of those captured widths in the NEW order - correct for a move
  anywhere in the list, not just a two-zone swap, and self-tested against exactly the
  user's own numeric example. `SampleKeymapControl` gained a zone-bar drag gesture
  (separate from the existing boundary-edge drag, which still takes priority): mouse-
  down in the zone-label strip starts a POTENTIAL reorder; mouse-up over the SAME zone
  is treated as a plain click-to-select (unchanged), mouse-up over a DIFFERENT zone
  fires the new `ZoneReordered` event.
  - **A real bug caught while writing the reorder self-test, before reporting done**:
    the FIRST `ZoneListSnapshot` design (from entry 15, storing plain field clones in
    list order) restores fields BY POSITION - correct for `MoveZoneBoundary` (order
    never changes there) but WRONG for a reorder's undo, since it would silently swap
    which KmpZone OBJECT holds which zone's data while leaving the list's order exactly
    as the edit left it. Any identity-bound reference (the tree's per-zone nodes,
    `_selectedZone`) would end up pointing at an object now holding the WRONG zone's
    data after undo - worse than not undoing at all, since it would look correct
    (right values, by position) while being wrong (attached to the wrong object).
    Redesigned `ZoneListSnapshot` to pair each snapshot entry with the LIVE `KmpZone`
    instance it came from (not just a clone) - `ApplyTo` restores each live object's
    OWN fields from its OWN paired clone, then rebuilds the list's order from those
    same live references. This also makes a future zone add/delete correct for free
    (an added zone simply isn't in the snapshot's entries and gets dropped by rebuild;
    a deleted zone's live reference is still there and gets added back), though neither
    is wired to undo yet (see Add Zone above).

New tests: `Core/Sample/SamplePhase10SelfTests.cs` gained a drag-reorder block (widths
preserved across the move, list order + widths restored by undo using the corrected
`ZoneListSnapshot`) using the user's own 10-wide/20-wide example; `Tools/
SampleEditorSmokeTest.cs` gained an end-to-end Add Zone + Import Sample block against a
real hardware fixture (placeholder appended and confirmed skipped, sample imported
bit-exact, key range unchanged by the import). Verification: build clean (same 4
pre-existing warnings), `--librarian-selftest` green, `--ui-theme-smoketest` green
(`SampleEditorWindow` itself constructs cleanly both times; one unrelated `LibrarianShell
Window` activation-timing check flaked once and passed on immediate rerun - pre-existing
OS-focus flakiness, not touched this round), `--sample-editor-smoketest` green end-to-end
including the new Add Zone/Import Sample block, `--sample-format-fixture-check
SampleFixtures` still 75/75 byte-identical, `--sample-editor-visual-check` against a real
stereo pair confirms the transport bar's icons/colors and the Add Zone/Import Sample/
Delete Zone button row all render correctly.

**Still needs a real click-through**: the transport bar's Rewind/FF step size and Pause/
Resume's one-shot-not-loop behavior, felt out on a real sample rather than reasoned about
in the abstract; the scrub-click grey line's visibility/contrast against the waveform
trace at a glance; Add Zone's default placeholder width (currently up to one octave -
may feel too large or too small in practice); and the zone drag-reorder gesture itself
(does grabbing the thin zone-bar strip feel discoverable, does the yellow drop-target
outline read clearly enough while dragging).

**17. Fifth UI feedback batch - a real data-loss bug (Delete key), a real silent-failure
bug (Kronos pull skipping content with no visible warning), reversed the previous
round's click-to-play design per explicit correction, and multi-collection tree
support.** Nine items, roughly in the order investigated:

- **Delete key deleted the WHOLE zone instead of the highlighted waveform range - the
  most serious item this round.** `OnWindowPreviewKeyDown` routed Delete unconditionally
  to `OnDeleteZone` regardless of whether a crop selection was active - highlighting a
  range and pressing Delete silently discarded the entire sample. `CutSelection`
  already existed and already recorded proper undo (used by the Cut context-menu item),
  it just was never reachable via the Delete key because zone-deletion always won the
  race. Fixed: Delete now cuts the selection when one exists, falling back to Delete
  Zone only when nothing's selected.
- **Ctrl+A still didn't select all.** Root cause: it was wired ONLY on
  `SampleWaveformControl.OnKeyDown`, which only fires when that SPECIFIC control
  instance has keyboard focus - exactly like Ctrl+Z/Ctrl+Y needed to be BEFORE they were
  promoted to `OnWindowPreviewKeyDown` (see entry 14). Moved to the same window-level
  handler; the per-control version was dead code once that's in place (PreviewKeyDown
  always tunnels first) and was removed.
- **Click-to-play was wrong per the user's explicit correction, not a bug in what was
  built - a REVERSAL of item 5 from entry 16.** Confirmed design: a plain click sets
  ONLY the grey scrub-line cursor (`SampleEditorViewModel.SetCursorFrame`), it must NOT
  start playback; the Play button/Space (`PlaySelectedSample`) now starts from that
  cursor position instead of always restarting at Sample Start/frame 0. `PlayFromFrame`
  (last round's auto-play-on-click entry point) is kept - it's still the right primitive
  for the transport bar's Rewind/FF/Locate/Resume, which SHOULD immediately audio when
  already playing/paused - just no longer wired to a plain click.
- **Stereo marker drag didn't move both panes together.** Same category of bug as entry
  16's crop-selection fix, for a DIFFERENT interaction: `SetMarker`'s stereo mirroring
  only applied once, on mouse-up - during the drag itself only the pane under the
  cursor moved. Added `SampleWaveformControl.MarkersChanging`, firing on every
  intermediate MouseMove of a Sample Start/loop-edge/whole-loop-region drag; the window
  mirrors `SampleStartFrame`/`LoopStartFrame`/`LoopEndFrame` directly onto the sibling
  pane, same "cheap DP-to-DP copy on every MouseMove" shape as
  `SelectionPreviewChanged`'s.
- **Keymap boundary line didn't sit at a key's actual edge for BLACK top keys.** Traced
  to real geometry, not a fuzzy alignment issue: the boundary was drawn at
  `leftX[topKey+1]` (the left edge of the NEXT key). For a WHITE top key that equals its
  own right edge (correct) - but a black key is drawn at only 60% width, CENTERED on
  the white-key grid boundary (entry 14's own layout design), so for a BLACK top key
  `leftX[topKey+1]` lands at that key's CENTER, not its edge. That inconsistency (edge
  for white, center for black) is exactly "doesn't sit in the center of each key /
  hard to see where the top note is." Fixed by switching to `rightX[topKey]`
  everywhere a boundary position is computed (rendering, hit-testing, AND the drag-time
  nearest-boundary scan) - well-defined for every key 0..127, so this also drops the
  old "topKey+1 might be out of range" special case. The zone-bar's own colored segment
  right edge was updated to match (`rightX[high]`, was `leftX[high+1]`) so the segment
  and the yellow line drawn over it never visually disagree about where a zone ends.
- **Add Zone collapsed the tree.** `SampleTreeNode.IsExpanded` existed as a bindable
  property but was never actually bound to the real `TreeViewItem` - added a
  `TreeView.ItemContainerStyle` binding it TwoWay. That alone wasn't enough:
  `RebuildTreeFromCollection` (every edit that changes the tree shape runs through this)
  used to unconditionally `Roots.Clear()` and rebuild everything from scratch as fresh
  node objects, which starts every TreeViewItem collapsed regardless of the binding.
  Rewrote it to replace only the ONE root matching the .KSC path it was given, and to
  carry forward which multisample nodes (matched by their stable .KMP path - object
  identity doesn't survive a rebuild) were expanded before rebuilding, restoring
  `IsExpanded` on the new nodes. Also only clears selection when the selection was
  actually inside the collection being rebuilt (`IsDescendant`), not unconditionally -
  needed once a second collection can be open and untouched (see below).
- **Loading a second .KSC replaced the first instead of adding a second tree entry.**
  `RebuildTreeFromCollection`'s rewrite above (rebuild/replace ONE root by path) is what
  makes this possible - `OpenCollection` no longer needs its own special case.
  `SampleTreeNode.Collection` became `CollectionRef` (a `(KscCollection, string Path)?`
  tuple, matching `MultisampleRef`/`ZoneRef`'s existing shape) so a root can report
  which .KSC it came from. `SelectNode` now also re-resolves "the active collection" for
  the collection-LEVEL operations (Export Collection, New Stereo Pair, Add Multisample,
  the normalization report) by walking up from whatever's selected to its owning root,
  rather than always targeting whichever collection was opened most recently - otherwise
  those four operations would silently keep acting on the FIRST collection even after
  the user navigated into a second one. Full library MERGING (the user's own "eventually
  we'll look at" framing) is still out of scope - this only lets two collections coexist
  in the tree and each be edited/saved independently.
- **No guard against closing with unsaved changes.** `_zoneDirty`/`_sampleDirty` existed
  but are scoped to "whatever's currently selected" and get reset by `SelectNode` on
  every navigation - a per-selection-only close guard would miss the reported scenario
  (edit item A, navigate to item B, close without saving A). Turned them into properties
  (not plain fields) whose setters ALSO mark a new session-wide `HasUnsavedChanges` flag
  that `SelectNode` does not reset - every existing `_zoneDirty = true`/`_sampleDirty =
  true` call site (well over a dozen, scattered across this file) picks this up
  automatically with no per-site changes needed. `SaveSelectedSample`/
  `SaveSelectedMultisample` clear it on success. Documented as best-effort (can go
  stale-true after everything's actually saved if save order didn't match edit order,
  never stale-false) - erring toward one extra confirmation is the right trade for a
  data-loss guard. `SampleEditorWindow` gained a `Closing` handler checking it via
  `MessageBox.Show`, same confirm pattern `LibrarianShellWindow` already uses.
- **Kronos KSC pull ("SCREAMINGHEAD-FINAL.KSC") reported "(2 entries)" but loaded
  nothing, though the Kronos itself shows content there.** Investigated via `--sample-
  ftp-pull-check` against the environment's own reachable test Kronos, but could NOT
  reproduce against the user's actual hardware/path (SSD2/DAVE STUFF/...) - that host
  isn't reachable from here, and the diagnostic hung waiting on a connection rather than
  failing fast, so this was not root-caused directly this round. What WAS found and
  fixed: both `SampleFtpClosure.PullAsync` (a failed .KMP/.KSF download during an FTP
  pull) and `RebuildTreeFromCollection` (a listed .KMP missing/unreadable on local disk)
  silently skipped the failure with only an `AppLog.Warn` entry - the user would see
  "Loaded 'X.KSC' (N entries)" (the .KSC's own raw entry count, unaffected by what
  actually resolved) over a tree that's empty or missing multisamples, with NO visible
  explanation. `PullAsync` now returns a `failures` list (which KMP/KSF, and why) that
  `SampleRemoteBrowserDialog` shows in a warning dialog right after a pull; `RebuildTree
  FromCollection` now appends the same kind of detail to `OpenCollection`'s StatusText.
  This won't fix whatever the actual root cause on the user's hardware is, but turns the
  failure from silent into something they can read and report back precisely.

New tests: `Core/Sample/SamplePhase11SelfTests.cs` (opening a second .KSC adds a root
rather than replacing the first; re-opening an already-open collection still replaces
only its own root; expansion state survives an edit-triggered rebuild; session-wide
`HasUnsavedChanges` survives `SelectNode(null)` and clears on save). A real bug caught
while writing these tests (test-fixture isolation, not production code): the session-
dirty test block originally reused `kscA`, which an EARLIER block's `AddPlaceholderZone`
call had already mutated on disk (a second zone), so a fresh re-open no longer had
exactly one zone - given its own dedicated collection ("PhaseC") instead. Verification:
build clean (4 pre-existing warnings), `--librarian-selftest` green (9 new checks),
`--ui-theme-smoketest` green, `--sample-editor-smoketest` green end-to-end,
`--sample-format-fixture-check SampleFixtures` still 75/75 byte-identical,
`--sample-editor-visual-check` confirms no rendering regressions from the keymap/tree
changes.

**Still needs a real click-through**: whether the keymap boundary line now visibly
tracks a black top key's own edge correctly at a glance (geometry says it should; not
seen rendered at a zoom level where a black-key boundary is actually visible); the
Delete-key fix's exact feel (does cutting-not-zone-deleting match expectations when a
selection exists); the unsaved-changes dialog's wording/behavior on a real close; and -
most importantly - a retry of the SCREAMINGHEAD-FINAL.KSC pull, which should now show a
specific warning identifying exactly which file(s) failed to download/open instead of
silently reporting success.

**18. SCREAMINGHEAD-FINAL.KSC diagnostic result, a real styling regression from entry
17, and a keymap redesign per explicit feedback.**

- **SCREAMINGHEAD-FINAL.KSC diagnostic result**: the warning added in entry 17 reported
  "NEWMS000.KMP (not found on disk)" for both referenced multisamples. Points at the
  `<kscDir>/<ksc-basename>/<kmpName>` folder-naming convention (documented as the
  format's own convention, hardware-confirmed for the fixtures this app was built
  against) not holding for this specific real file - most likely the .KSC was renamed
  on the Kronos at some point without its content folder being renamed to match, which
  this app's naive basename-matching can't see. NOT fixed this round (would need
  confirming the real remote/local folder name first, to avoid guessing a fallback that
  papers over a genuine organization mismatch) - see the reply to the user for the
  specific follow-up question.
- **Tree font color regression - entry 17's own `TreeView.ItemContainerStyle` addition
  broke it.** A `<Style TargetType="TreeViewItem">` with no `BasedOn` REPLACES the
  app-wide implicit `TreeViewItem` style (`Themes/Dark.xaml`) for that TreeView, not
  extends it - silently dropping that style's `Foreground="{StaticResource TextBrush}"`
  (among other setters) and falling back to WPF's stock template (black text on the
  app's dark background). Fixed with `BasedOn="{StaticResource {x:Type TreeViewItem}}"`.
- **Keymap "border lines are confusing" - replaced with a faint greyscale key highlight,
  per explicit direction.** The permanent grey divider line drawn at EVERY zone boundary
  (spanning the full piano height, regardless of hover/selection state) is gone;
  boundary lines now render ONLY while active (hovered or being dragged, still yellow) -
  the line remains the actual resize-drag hit target either way (`HitTestBoundary`
  doesn't care whether it's currently drawn), so resizing is unaffected, only the
  constant-clutter rest state is gone. In its place, `SelectedZone`'s own key range
  (`leftX[low]` to `rightX[high]`, spanning the piano rows only) gets a faint
  `Color.FromArgb(70, 200, 200, 200)` fill - confirmed in the visual check as a subtle
  tint over the selected zone's keys, distinct from the zone bar's own existing
  (unchanged, per the user's explicit "can remain as is") selection highlight above it.

Verification: build clean (4 pre-existing warnings), `--librarian-selftest` green,
`--ui-theme-smoketest` green, `--sample-editor-smoketest` green end-to-end,
`--sample-format-fixture-check SampleFixtures` still 75/75 byte-identical,
`--sample-editor-visual-check` confirms both the tree's text color restored to light
and the new faint key-highlight rendering visibly over the selected zone's own keys
(distinct from the zone bar's unchanged highlight above).

**19. Sixth UI feedback batch - a real stereo-parity bug (Add Zone silently dropping a
pair to mono), the tab framework, and eight smaller fixes/additions.** Eleven items,
worked in the order listed:

- **Removed the "Loop Preview" checkbox.** Redundant per explicit direction - Loop
  Enabled alone already loops on Play (entry 17's own design), so the separate UI-only
  toggle only added a second control that did the same thing. `SampleEditorViewModel.
  LoopPreviewEnabled` and its `|| SampleLoopEnabled` check in `PlaySelectedSample` were
  removed outright rather than left as dead code.
- **Unload Collection.** New `SampleEditorViewModel.UnloadCollection(kscPath)` removes
  one open root from the tree (session-only - nothing on disk is touched, so it can
  always be re-opened). Reachable two ways: File > Unload Collection (targets the
  active collection, via a new `ActiveCollectionPath` property) and a new right-click
  context menu on the tree itself ("Unload KSC"), built on the fly in `OnTreeContext
  MenuOpening` since it needs to resolve which root the clicked node belongs to (new
  `FindOwningCollectionPath`, same walk-up-to-root resolution `SelectNode` already uses
  for "the active collection"). Right-click had to first select the node under the
  cursor (`OnTreeRightButtonDown`/`PreviewMouseRightButtonDown`) - WPF doesn't do this
  automatically for a TreeViewItem the way it does for a plain click.
- **NEWMS000.KMP/NEWMS001.KMP missing-multisample warnings suppressed.** These are the
  Kronos's own default placeholder names, always present (and often unpopulated) on a
  brand-new library - a missing or unreadable one is the NORMAL state, not a real data
  problem. New `SampleEditorViewModel.IsIgnorablePlaceholderKmp` (internal, shared with
  `SampleFtpClosure`) matches on the .KMP's base filename; any OTHER missing/unreadable
  .KMP still warns exactly as before, in both `RebuildTreeFromCollection` (local reload)
  and `SampleFtpClosure.PullAsync` (remote pull, entry 17's own failure-visibility work).
- **Top Key floor + a real stereo-parity bug in Add Zone.** `ApplyZoneEdits` (the manual
  Top Key text field) now floors the typed value at the PREVIOUS zone's own Top Key + 1,
  matching the invariant the keymap's boundary-drag already enforced by construction but
  the text field never checked. Separately - and the most serious find this round -
  **Add Zone in a stereo multisample silently broke stereo parity**: `AddPlaceholderZone`
  only ever touched the ONE multisample the user was looking at, shrinking its last
  zone's key range to carve out room for the new placeholder - but never touched the
  stereo sibling (-L/-R pair), so the two halves' key ranges drifted out of exact sync.
  `ResolveStereoPartner` matches a partner by EXACT (OriginalKey, TopKey) - once they
  diverged, re-selecting the shrunk zone (or any zone whose range no longer matched)
  silently stopped resolving its stereo partner at all, dropping the shared L/R waveform
  view back to plain mono with no error or warning. This is the root cause of item 8's
  "adding a zone in a stereo sample goes to mono view" report. Fixed: when the target
  multisample is a resolved -L/-R half (`SampleImportBuilder.FindStereoSibling`),
  `AddPlaceholderZone` now applies the identical key-range change (the same shrunk
  previous-zone TopKey, the same new zone's OriginalKey/TopKey) to the sibling too,
  best-effort gated on the sibling's zone count matching the primary's BEFORE the add (a
  well-formed pair) - confirmed end-to-end via `--sample-editor-visual-check`'s new Add
  Zone step against a real stereo pair in the ANDRE_K2_73 fixture (status text: "Added an
  empty zone (G2-G2) to both stereo channels"; both NEWMS000/NEWMS001 tree branches
  showed the new zone).
- **Add Zone's focus/selection bug.** Root cause was `RebuildTreeFromCollection`'s own
  `SelectNode(null)` (needed to drop stale selection - see its own comment) leaving
  NOTHING selected after the tree rebuild Add Zone triggers, which is what visually read
  as the click "snapping back to the parent." `AddPlaceholderZone` now returns the
  target multisample's .KMP path (was `void`); `OnAddPlaceholderZone` uses it to find
  that multisample's node in the REBUILT tree and re-select its LAST child (the new
  zone always lands there) via the existing `SelectTreeNode` - reference identity can't
  be used since the rebuild re-reads from disk into brand-new `KmpZone` objects. This
  also directly fixes item 4's other half: the new zone's own interior boundary is only
  ever draggable once its multisample is back in context (`CurrentMultisampleZones`),
  which re-selecting it now guarantees immediately.
- **Keymap key-highlight contrast.** `Color.FromArgb(70, 200, 200, 200)` moved a white
  key's near-white fill (`0xE8E8E8`) by only ~9 of 255 luminance levels - basically
  invisible in practice, though still visible enough over the much-darker black keys to
  not be obvious everywhere. Changed to `Color.FromArgb(100, 140, 140, 140)` - darker
  and more opaque, shifts a white key ~35-40 levels while staying a subtle tint over
  black keys. Confirmed via `--sample-editor-visual-check` screenshots (leftmost
  selected-zone keys visibly greyer than the unselected white keys beside them).
- **Tab framework.** The right-hand detail pane's flat wall of sections is now a
  `TabControl` (`EditorTabs`) with three tabs, per explicit direction and naming -
  "Keymap" (the piano-strip zone-assignment control, moved out of its own always-visible
  section), "Samples" (name/frames, Playback Format minus Loop Enabled, the waveform/VU/
  transport/Fade/TempoPitch editing surface - unchanged content, just relocated), and
  "Looping" (a new "LOOP" field section: Loop Enabled moved here from Playback Format,
  plus Sample Start/Loop Start/Loop End/Use Zero/Loop Lock/Reverse Loop) - "looping is
  separate from editing," per explicit direction. Zone identity (Filename/Original Key/
  Top Key/Add Zone/Import Sample/Delete Zone) and Split L/R stay OUTSIDE the tabs, above,
  since they're "which zone am I editing," not a category of editing operation -
  Content is placeholder-organized only ("we'll discuss what the tabs are later," per
  the ask) - no new fields or behavior invented, existing controls kept every x:Name/
  event wire-up unchanged, just moved. `SamplePanel`'s existing `HasSampleLoaded`
  visibility gate now also applies to a new `LoopingPanel` the same way. Did NOT rewrite
  the field-row WrapPanels into a rigid Grid layout - entry 14's own design comment
  documents WrapPanel-per-field-pair as deliberate (lets rows reflow at the window's
  down-to-900px MinWidth without splitting a label from its box); doing that would
  regress a documented past decision, not fulfill "align to a grid." Confirmed via
  `--sample-editor-visual-check`, which now clicks through all three tabs and
  screenshots each.
- **Keymap selection awkwardness.** Root cause: `HitTestBoundary`/the resize (SizeWE)
  cursor applied across the WHOLE control height, not just the zone-bar header - so a
  piano-key click whose x-position happened to line up with a boundary started a
  resize DRAG instead of selecting that key's zone, and the cursor changed to a resize
  arrow while hovering piano keys nowhere near the actual header. Fixed in both
  `OnMouseMove` (hover/cursor) and `OnMouseLeftButtonDown` (drag-start): boundary
  hit-testing is now gated on `pos.Y < ZoneBarHeight` - a piano-key click (y >=
  ZoneBarHeight) ALWAYS resolves to `ZoneClicked`, regardless of x, and the resize
  cursor only ever appears over the header strip.
- **Edit menu: Revert KSC Changes / Revert ALL Changes.** New `SampleEditorViewModel.
  RevertActiveCollectionChanges()` re-reads the active collection's .KSC (and every
  referenced .KMP) fresh from disk, replacing just that one root via the existing
  `RebuildTreeFromCollection` - every other open collection is untouched, and any
  unsaved zone/sample edit under that root is discarded since the rebuild replaces every
  `KmpMultisample`/`KmpZone`/`KsfSample` instance with a fresh disk read. New `RevertAll
  Changes()` closes every open collection and resets every piece of session state
  (undo/redo stacks, the remote pull map, session-dirty flags) - the literal "start from
  scratch" the user asked for. Both menu items confirm via `MessageBox` before discarding
  anything, mirroring the existing Closing-window guard's pattern.
- **Sample Editor smoke/visual-check tooling.** `Tools/SampleEditorVisualCheck.cs` now
  copies its target fixture into a scratch dir before opening it (same discipline
  `SampleEditorSmokeTest` already applies) - needed because it now does a real Add Zone
  button click (`BtnAddZone.RaiseEvent(... ButtonBase.ClickEvent)`) that actually writes
  to disk, where every earlier step was tree-selection-only and read-only. Confirmed the
  real ANDRE_K2_73 source fixture was NOT touched by this (`git status`/`git diff
  --stat` clean for `SampleFixtures/`) before relying on it going forward.

New tests: `Core/Sample/SamplePhase12SelfTests.cs` - stereo Add Zone mirroring (key
ranges match exactly on both halves after the add, stereo-partner resolution survives
re-selecting the shrunk zone afterward), the Top Key floor (clamped at previous zone's
TopKey+1, unclamped above it), NEWMS000/NEWMS001 suppression (vs. a real missing .KMP,
which still warns), Unload Collection (removes only the targeted root, clears the active
path only when unloading the active collection), Revert KSC Changes (discards an
unsaved edit, leaves a second untouched collection alone), and Revert ALL Changes
(clears every root/the active path/session-dirty/undo state). Verification: build clean
(4 pre-existing warnings), `--librarian-selftest` green (SamplePhase12's new checks
included), `--ui-theme-smoketest` green, `--sample-editor-smoketest` green end-to-end,
`--sample-format-fixture-check SampleFixtures` still 75/75 byte-identical,
`--sample-editor-visual-check` against a real stereo pair in the ANDRE_K2_73 fixture
confirms the tab framework (all three tabs render their relocated content), the
brighter key highlight, and - most importantly - a real Add Zone button click landing
tree selection on the new zone (not its parent) with the stereo-mirror status message
and both L/R tree branches showing the new zone.

**Still needs a real click-through**: the tree's right-click "Unload KSC" context menu
and the File > Unload Collection / Edit > Revert KSC|ALL Changes menu items (all wired
and building cleanly, none screenshot-tested since WPF menu popups don't capture well
via the same PrintWindow approach the rest of this tool uses); whether the keymap's
resize-cursor-only-in-header fix feels right in practice (geometry says it should; not
felt out with a real mouse); the "Samples"/"Keymap"/"Looping" tab split itself - explicitly
provisional, pending further direction on what belongs where.

**20. Removed the redundant frame-count "Apply Fade" - highlighting IS the fade
range now.** User report: highlighting a section and clicking "Apply Fade" did nothing;
it only ever did anything after manually typing numbers into separate Fade In (frames)/
Fade Out (frames) boxes - and pointed out those boxes were redundant given the whole
point of highlighting a range.

- **Root cause**: the Samples tab had TWO independent, unconnected fade mechanisms.
  `SampleEditorViewModel.ApplyFadeInSelection`/`ApplyFadeOutSelection` (right-click the
  waveform > Fade In/Fade Out) already ramped gain across exactly the current
  SELECTION - correct, and already tested. But the ONLY fade control visible without
  right-clicking was "Fade In (frames)"/"Fade Out (frames)" + "Apply Fade", wired to a
  completely different method (`ApplyFade(int, int)` -> `FadeEffect`) that ramped from
  the EDGES of the WHOLE buffer by a typed frame count, entirely ignoring
  SelectionStartFrame/SelectionEndFrame. Both fields defaulted to "0", so clicking
  "Apply Fade" without first typing real numbers applied a 0-frame fade to each edge -
  a no-op, which is exactly "nothing happens."
- **Fix**: deleted the frame-count fade path outright rather than gate/reroute it -
  `ApplyFade`, `FadeInBox`/`FadeOutBox`/`BtnFade`/`OnFade`, `Core/Sample/Dsp/
  FadeEffect.cs`, and its self-test block in `SampleDspSelfTests.cs` are all gone (a
  quick grep confirmed `FadeEffect` had no other callers once `ApplyFade` was removed -
  fully dead code, not just unused UI). The Edit toolbar's WrapPanel gained visible
  **Fade In**/**Fade Out** buttons (next to Crop to Selection/Normalize/Trim Silence) -
  reusing the EXISTING `OnWaveformFadeIn`/`OnWaveformFadeOut` handlers unchanged (same
  Click delegate signature as the right-click context menu items already used), so
  there's exactly one fade implementation now, reachable two ways. Matches this
  toolbar's existing pattern: like Crop to Selection, the buttons aren't disabled
  without a selection - clicking without one reports "Select a range in the waveform
  first" via the existing `ApplySelectionFade` guard, same as every other
  selection-gated action here.
- `Tools/SampleEditorSmokeTest.cs`'s DSP-edit block, which used to exercise the deleted
  `ApplyFade(100, 100)`, now sets a real selection and exercises `ApplyFadeInSelection`/
  `ApplyFadeOutSelection` instead - same DSP-edit-then-undo coverage, now pointed at the
  code path that's actually reachable from the UI.

Verification: **could not run `dotnet build`/any of the headless diagnostics this
round** - `dotnet --list-sdks` on this machine now reports only 9.0.313; the two
`net10.0`-capable SDKs on disk (`C:\Program Files\dotnet\sdk\10.0.204` and `\10.0.302`,
which this repo targets) are broken/incomplete installs (each folder contains only a
`Roslyn\` subdirectory - no `dotnet.dll`/MSBuild), not something this session did or can
fix without a system-level SDK repair/reinstall. All changes were reviewed by hand
instead: a whole-repo grep for every removed symbol name (`ApplyFade(`, `FadeEffect`,
`FadeInBox`, `FadeOutBox`, `BtnFade`, `OnFade`) turns up zero remaining references in
any `.cs`/`.xaml` source file (only stale `obj/` generated code from the last successful
build, harmless - regenerated on the next real build). **Flagged to the user; owed
before calling this done**: an actual `dotnet build` + `--librarian-selftest` +
`--sample-editor-smoketest` run once a working SDK is available, and a real click-through
(highlight a range, click the new Fade In/Fade Out buttons, confirm the fade lands
exactly on the highlighted range and nowhere else).


**21. Bug sweep + UI/UX pass over the Sample Editor.** Asked for a sweep for bugs,
the features a sample editor is normally expected to have, and a tightening of the
UI/UX. Full written findings report (including the items deliberately NOT actioned)
lives outside the repo; everything actioned is below.

**Entry 20's debt is closed first**: `dotnet --list-sdks` now reports a working
`10.0.400`, so the fade-removal from entry 20 - which landed unbuilt during the SDK
breakage - is build-verified and self-test-verified as part of this round.

**Tier 1 - wrong bytes or lost work:**

- **Unsaved sample edits were destroyed by clicking another tree node.** `SelectNode`
  nulled `_selectedSample` and re-read the `.KSF` from disk; nothing held the edit. Every
  unsaved crop/fade/normalize/cut/loop-point change on the previous zone vanished with no
  prompt - and `_sessionSampleDirty` stayed true, so the close-guard then warned about
  edits that no longer existed anywhere.
- **In stereo Combine mode the partner was never saved.** Every mirrored edit wrote to
  `_partnerSample`, but `SaveSelectedSample` only ever wrote `_selectedSamplePath`, so
  the pair silently diverged on disk and all the careful mirroring was discarded at save
  time.
- **`HasUnsavedChanges` went stale-negative**, contradicting its own comment's promise
  ("never stale-NEGATIVE"): zone edits mutate the LIVE `KmpZone` objects and survive
  navigation, so saving multisample B cleared the one global flag that also covered an
  unsaved multisample A.

  All three are fixed by one mechanism rather than three patches: `_dirtySamples` /
  `_dirtyMultisamples`, keyed by file path and holding the LIVE edited objects.
  `SelectNode` (and `ResolveStereoPartner`) read pending edits back instead of re-opening
  the file; Save writes every pending entry rather than just the selection;
  `HasUnsavedChanges` is now exact instead of best-effort. Enrolment hangs off the
  existing `_zoneDirty`/`_sampleDirty` property setters, so all dozen-plus edit call
  sites were untouched. `RebuildTreeFromCollection` keeps a pending multisample's live
  object instead of re-reading it (otherwise an unrelated Add Zone would drop another
  multisample's unsaved zone edits while leaving them registered as pending);
  Unload/Revert KSC/Revert ALL discard pending edits scoped by path prefix.

- **Three more instances of entry 19's stereo-parity bug class.** Entry 19 fixed Add
  Zone; `ApplyZoneEdits`, `MoveZoneBoundary` and `ReorderZone` all still mutated ONE
  half's key ranges. `ReorderZone` is the worst - it rewrites every `TopKey`, so it broke
  `ResolveStereoPartner`'s exact `(OriginalKey, TopKey)` match for the whole multisample
  at once, silently dropping the shared L/R view back to mono. `DeleteSelectedZone` also
  now mirrors (and is finally undoable - it was the one zone edit with no undo at all).
  - **Not** via `SampleImportBuilder.FindStereoSibling`: that re-OPENS the sibling `.KMP`
    from disk and returns a fresh object, which is correct for Add Zone (it saves
    immediately) and wrong for a live in-memory edit - mirroring onto a copy the tree
    doesn't hold makes the change invisible and then discards it. New
    `FindLiveStereoSibling` walks the tree for the live instance;
    `ResolveStereoPartner` now prefers it too, with the disk lookup as fallback.
  - `ZoneListSnapshot` now spans one OR MORE zone lists so a mirrored edit undoes as one
    atomic step. Undoing only the clicked half would have re-introduced exactly the
    divergence the mirroring exists to prevent.
- **Cut/Paste didn't mirror to the stereo partner** - the only two length-changing edits
  that bypassed `ApplyEffect`, splicing `_selectedSample`'s array inline. L and R ended
  up different lengths, `Interleave` padded the shorter one, and everything after the
  edit point played back time-offset between channels. Both now route through new
  `DeleteRangeEffect`/`PasteRangeEffect` (`Core/Sample/Dsp/SpliceEffects.cs`), so the
  mirroring/undo/marker-clamping are structural rather than remembered per call site.
- **Loop markers were never clamped when an edit shortened the buffer.** `KsfSample.
  ToBytes` deliberately writes `SampleStart`/`LoopStart`/`LoopEnd` verbatim (it must -
  a header-only file's stale `LoopEnd` is real recoverable data, doc §3.3) and its own
  comment puts re-deriving them on "callers that resize Pcm". No caller did, so a crop
  wrote out-of-range loop points straight into the `.KSF`. Playback never showed it
  (`LoopingSampleProvider` re-clamps in its constructor); the file carried the damage.
  New `ClampMarkersToBuffer`, called from `ApplyEffect` and `ApplyTempoPitch`, skipping
  header-only samples so §3.3's preserved value isn't destroyed.

**Tier 2 - visible malfunction:**

- **`PlaybackStopped` race left the UI thinking playback had stopped.** Every Play* entry
  point calls `Stop()` first, which raises `PlaybackStopped`; the VM handler adds a
  Dispatcher hop, so the stale `false` landed AFTER the restart had set `IsPlaying =
  true`. Rewind/Fast-Forward mid-playback reverted the Play/Stop button, greyed out
  Pause, and made the playhead line vanish (it's gated on `IsPlaying`) while audio kept
  running. Fixed with a generation token in `SamplePlayback`: a stop-for-restart is
  silent, a genuine end-of-buffer stop still fires.
- **No-op `LostFocus` commits pushed undo steps and marked the file dirty.** `LostFocus`
  fires on every focus change, so tabbing across Sample Start/Loop Start/Loop End
  produced three dead undo entries and a window claiming unsaved changes. `SetMarker`
  now compares after the full clamp/snap/lock pipeline (so "typed a value that clamps
  back to where it already was" counts as a no-op too); `SetLoopEnabled` and
  `ApplyZoneEdits` got the same guard.
- **`ApplyTempoPitch` was unbounded** - only `tempo <= 0` was rejected, so a typo'd
  `0.001` asked for ~1000x the buffer (hang/OOM). Clamped to 0.25-4x and ±24 semitones,
  and the clamp is reported rather than silent.
- **`ApplyZoneEdits` had a floor but no ceiling.** Entry 19 added "can't go below the
  previous zone's Top Key + 1"; there was no matching cap, so typing a high value left
  the NEXT zone with an inverted range. The cap (`next.TopKey - 1`) is not new behaviour
  - it is exactly what `SampleKeymapControl.cs:211` already clamped a boundary DRAG to;
  the typed field was the one path that skipped it. `Tools/SampleEditorSmokeTest.cs`
  asserted the old unclamped behaviour and was updated to assert the clamp instead.
- **Culture-sensitive `double.TryParse` on Tempo/Pitch** - the boxes are seeded "1.0"
  from XAML, which a comma-decimal locale reads as TEN. Now `InvariantCulture`, and an
  unparseable field says so instead of silently substituting.

**UI/UX:**

- **The waveform, ruler, scrollbar, transport, VU/volume and the whole Edit toolbar were
  trapped inside the "Samples" tab.** That made the core loop of a sample editor
  impossible: switching to "Looping" to type a loop point hid the transport (couldn't
  audition the loop being set) AND the waveform (couldn't see the markers being moved).
  All of it is hoisted above the `TabControl`. The tab TAXONOMY is deliberately
  unchanged - still pending direction, per entry 19.
- **Empty tabs when nothing is loaded**: `RefreshDetailPanels` collapsed the
  `StackPanel`s INSIDE the `TabItem`s, leaving three clickable blank tabs above the
  "Select a zone..." hint. The `TabItem`s themselves (and the whole `TabControl`, whose
  chrome draws even when every item is collapsed) are now collapsed, and a selected-but-
  collapsed tab reselects a visible one.
- **The stereo rows reserved their space for a mono sample.** A fixed `RowDefinition`
  doesn't shrink when its child is Collapsed, so a mono sample kept ~172px of dead grey
  where the R pane would be. Row heights are now driven alongside the Visibility.
- **The Zone row couldn't wrap** - a horizontal `StackPanel` holding a variable-length
  filename plus two key fields and three buttons, clipping the buttons off the right edge
  at the window's own MinWidth. Now a `WrapPanel` with label+field pairs grouped, matching
  every other row in the window.
- Title bar shows the filename and a `*` dirty marker; Save Changes / File > Save Changes
  disable when there's nothing pending. Selection info now shows duration alongside the
  raw frame count.

**Features added** (all reachable from the Edit menu, most also as toolbar buttons):

- **Zoom In / Out / Zoom to Selection / Fit** - zoom was wheel-and-double-click only,
  mentioned nowhere but inside a status-bar sentence, and Zoom to Selection had no
  gesture at all. All drive the existing `SetView`, so ruler/scrollbar/sibling pane stay
  in sync for free.
- **Playhead follow** - the view pages to keep the playhead visible when zoomed in
  (previously the line left the view immediately and never came back).
- **Ctrl+X/C/V/A** plus Edit-menu entries for Cut/Copy/Paste/Select All - previously
  right-click-only, with Ctrl+A working but appearing nowhere in the UI. Ctrl+S, Ctrl+O,
  Ctrl+Plus/Minus/0, Home/End added; Ctrl-chords stay live with focus in a text field
  (the FieldBox style already disables the TextBox's own undo for exactly this reason).
- **Reverse, Silence Selection, Insert Silence, Remove DC Offset** - new
  `ReverseEffect`/`SilenceEffect`/`InsertSilenceEffect`/`DcOffsetEffect`, all through
  `ApplyEffect` so stereo mirroring, undo and marker clamping come free.
- **Arbitrary-gain dialog** (the right-click menu only offers ±1/3/6 dB presets).
- **Drag and drop** onto the window: `.KSC`/`.KMP` open, audio files import as a new zone
  through the same key prompts (now factored into one `PromptForZoneKeys`, previously
  copy-pasted in three handlers).

**Deliberately NOT added: loop crossfade.** It needs a `.KSF` byte that has never been
hardware-confirmed, and this codebase's no-speculative-fields rule already keeps Reverse
Loop preview-only for exactly that reason.

Verification: clean `dotnet build`; `--librarian-selftest` green including a new
`Core/Sample/SamplePhase13SelfTests.cs` (9 blocks - edit-survives-navigation, both stereo
channels saved, Cut/Paste mirroring, all three zone-edit mirrors, paired zone undo,
marker clamping reaching the file, exact dirty tracking, no-op guards, tempo bounds);
`--ui-theme-smoketest` green; `--sample-editor-smoketest` green end-to-end including the
new Top-Key-ceiling assertion; `--sample-format-fixture-check` still **75/75
byte-identical**; `--sample-editor-visual-check` screenshots confirm the hoisted
waveform/transport, the wrapped Zone row, the filename+dirty title, and - on a skipped
zone - only the Keymap tab showing with no blank tabs or dead space. Fixtures confirmed
untouched (`git status` on `SampleFixtures/` clean). The new self-tests were negative-
controlled (a deliberately failing check was confirmed to fail the run) rather than
trusted for passing on the first attempt.

**Still needs a real click-through**: the `PlaybackStopped` race fix (press Rewind/
Fast-Forward mid-playback and confirm the button stays on Stop, Pause stays enabled and
the playhead keeps moving) - no headless check can observe it; playhead-follow while
zoomed in; drag-and-drop of each accepted file type; and whether the loop fields now sit
close enough to the waveform in practice, since with a stereo pair the tabs land below
the fold at the default window height and need a scroll to reach.

**21a. Follow-up within the same round - three defects the above verification
structurally could not catch, found by review and each confirmed by negative control.**

- **BLOCKER, introduced by 21 itself: Add Zone lost the mirrored zone when the sibling
  already had a pending edit.** `AddPlaceholderZone` (and
  `ImportStereoAudioAsNewZonePair`) still mirrored onto
  `SampleImportBuilder.FindStereoSibling`'s FRESH DISK object. That was correct before
  this round, but 21 added the rule that `RebuildTreeFromCollection` KEEPS a pending
  multisample's live object instead of re-reading. So: mirrored key edit on -L (which
  registers -R as pending) → Add Zone on -L → the new zone is written to a fresh disk -R,
  then the rebuild keeps the PENDING -R, which never got it. Halves out of parity, the
  exact `(OriginalKey, TopKey)` stereo match broken, and a later Save writing the pending
  -R back over the zone just added. Both now prefer `FindLiveStereoSibling` with the disk
  lookup as fallback. The asymmetry that hid this: `AddPlaceholderZone` never sets
  `_zoneDirty`, so only the sibling diverged.
  - Every immediate `m.Save(path)` in the add/import paths now goes through a new
    `SaveMultisampleNow`, which also retires that path's pending-save registration -
    otherwise the registry keeps a stale claim that the rebuild rule would honour.
- **`DiscardPendingEditsUnder` matched a bare path prefix.** Collections `Foo.KSC` and
  `FooBar.KSC` have content dirs `.../Foo` and `.../FooBar`, so unloading or reverting
  **Foo** silently discarded FooBar's pending edits. Now compares on a whole directory
  (trailing separator), which the self-tests couldn't have caught since every fixture
  used a distinct prefix - a `Foo`/`FooBar` pair was added specifically for it.
- **Title bar and Save button went stale immediately after saving.** `UpdateWindowTitle`
  runs only from `RefreshDetailPanels`, but the three save handlers called only
  `UpdateStatus()`, so the `*` and the enabled Save Changes button persisted after a
  successful save. Also: Ctrl+S deliberately fires ahead of the focus-in-a-TextBox guard,
  so a typed-but-uncommitted field wasn't included - `OnSaveChanges` now moves focus off
  the field first, which triggers its `LostFocus` commit.

**`--sample-editor-visual-check` was itself wrong and is fixed.** After the hoist the
tabs sit below the fold, so the three per-tab screenshots were all taken from the top of
the scroll and captured the waveform with NO tab content in frame - three near-identical
images attesting to nothing (the tell was two output PNGs having identical byte sizes).
Every tab shot now scrolls to the end first; `05f_samples_tab_top` is the one
deliberately-unscrolled shot of the hoisted waveform/transport block. The re-run confirms
the central UX claim directly: the Looping tab's loop fields, the waveform, the transport
and the zoom/edit toolbar are all visible **at the same time**, which was impossible
before.

**Known limitation, stated rather than fixed: undo depth doesn't survive navigation even
though the edit now does.** `SelectNode` still resets `_sampleUndo` and drops the
Sample-domain entries from `_undoDomains`. That was self-consistent while the edit was
destroyed too; now the edit persists via `_dirtySamples` but its history doesn't, so
returning to a zone shows the crop but can't Ctrl+Z it. Not corruption - a later edit
snapshots the post-crop state correctly - just lost depth. Fixing it properly means
scoping the undo stacks by path alongside the cache, and `_undoDomains` is a single
global ordering that interleaves Sample and Zone entries, so it isn't a local change.
Deliberately deferred rather than half-done.

Verification after 21a: clean build, `--librarian-selftest` green (Phase13 now 11 blocks,
including the pending-sibling Add Zone case and the Foo/FooBar prefix case),
`--ui-theme-smoketest` green, `--sample-editor-smoketest` green, fixture check still
**75/75 byte-identical**, `SampleFixtures/` clean per `git status`, and the visual check
re-run and re-read. Both new self-test blocks were negative-controlled: reverting only the
`FindLiveStereoSibling` change in `AddPlaceholderZone` fails
`pendingsibling-key-ranges-still-in-parity`, `pendingsibling-earlier-edit-survived` and
`pendingsibling-partner-still-resolves`, confirming the blocker was real and the test
actually detects it.

**22. Insert Silence dialog: linked Frames/Seconds fields (2026-08-21, follow-up
to entry 21).** User request: the Insert Silence prompt should also accept a duration
in seconds, with the two fields kept in sync live - only frames are actually applied,
seconds is a second entry point onto the same value.

- New `Views/InsertSilenceDialog.xaml(.cs)` (replaces the generic `PromptDialog` call
  entry 21 used) - same `LoginDialog`-style two-field layout, not bolted onto
  `PromptDialog` itself since that's a single-field dialog reused in ~8 unrelated call
  sites. `Frames` is the only value exposed to the caller
  (`SampleEditorWindow.OnInsertSilence`); `ApplyInsertSilence` never sees seconds.
- Both `TextBox`es' `TextChanged` recompute the other, guarded by a `_syncing` flag so
  writing one box's `Text` (which fires `TextChanged` synchronously even unshown) doesn't
  loop back into itself. Seconds→frames rounds (`Math.Round`), not truncates, so
  "0.5s @ 44100Hz" round-trips back to exactly "0.5" rather than drifting a hair short.
  Parsed/formatted with `InvariantCulture` throughout - same reasoning as entry 21's
  Tempo/Pitch fix (seeded "0.25" must not read as a different number on a comma-decimal
  locale).
- Wired into `Tools/UiThemeSmokeTest.cs` twice: once via the existing `Try()` (construct
  cleanly), and once as a dedicated behavioral check that actually drives the two boxes
  (seed 11025 frames @ 44100Hz → "0.25"; set frames to 22050 → seconds becomes "0.5"; set
  seconds to "2" → frames becomes "88200") - `Try()` alone only proves XAML/resources
  resolve, not that the linking logic works. **Negative-controlled**: temporarily
  breaking the frames→seconds direction made this check fail with exactly that
  direction's assertion false and the other two true, confirming it actually exercises
  the link rather than passing by construction.

Verification: clean `dotnet build`, `--ui-theme-smoketest` green (including the new
behavioral check, confirmed via negative control), `--librarian-selftest` green,
`--sample-editor-smoketest` green, `--sample-format-fixture-check` still 75/75
byte-identical. **Not yet visually confirmed in a running window** - the smoketest proves
construction and the sync logic, not what it looks like; owed a real click-through
(type into each box, confirm the other updates, confirm OK still only accepts a positive
whole frame count).

**23. Field label / field vertical alignment fix (2026-08-21, follow-up to entry 21).**
User report: "Filename:", "Original Key:", "Sample Rate:" and most other field labels
sit visibly lower than the box/value they describe.

- **Root cause**: the `FieldLabel` style (used by all 11 field labels in this window) had
  `Margin="0 0 6 0"` - no bottom margin - while every sibling control in the same row
  (`FieldBox`, the value `TextBlock`s like `ZoneFilenameText`/`SampleNameText`, and every
  button) had a 6px bottom margin. Everything in these rows is `VerticalAlignment="Bottom"`
  and, since entry 21's WrapPanel change, stretched to a shared row height - so a 0
  bottom margin against a 6px one sits the label 6px lower than its own field, which is
  exactly the offset reported. One `SampleWarningText` was independently inconsistent the
  same way (no bottom margin against its row-mate `SampleFramesText`'s 6px).
- **Fix**: `FieldLabel`'s bottom margin changed to 6, matching everything else in the
  window; `SampleWarningText` given the same. A single style-level fix rather than
  touching each of the 11 label instances, since they all shared one style. Verified by
  grep that every `VerticalAlignment="Bottom"` element in the file now carries a 6px
  bottom margin - no remaining outliers.

Verification: clean `dotnet build`, `--ui-theme-smoketest` green,
`--librarian-selftest` green, `--sample-editor-smoketest` green,
`--sample-format-fixture-check` still 75/75 byte-identical, and
`--sample-editor-visual-check` screenshots read back directly - confirms "Filename:",
"Original Key:", "Top Key:", "Name:", "Frames:", "Sample Rate:", "Tempo x:",
"Pitch (semitones):", "Sample Start:", "Loop Start:" and "Loop End:" all now sit flush
with their fields across the Zone panel, the waveform info row, and both the Samples and
Looping tabs.

**24. Three follow-up bugs (2026-08-21): stereo view too strict, FTP dialogs truncate
errors, Delete Zone permanently disables.**

**1. Stereo waveform view required an exact key-range match, and shouldn't.**
`ResolveStereoPartner` only resolved a stereo partner via an EXACT `(OriginalKey,
TopKey)` match against the sibling's zones. Real, hand-edited or hand-pulled content
routinely has the two channels split at different points - still legitimately a stereo
pair, but the exact-match-or-nothing rule silently dropped it to a mono view. Now falls
back to the SAME INDEX in the sibling's own zone list when no exact match exists - the
same correspondence `ResolveSiblingZonesFor` already uses for mirroring key-range edits,
so display and editing now answer "which zone is this zone's partner" the same way.
Pinned by a new self-test fixture with deliberately mismatched L/R key ranges (0-60 vs
0-50); negative-controlled (reverting the fallback fails `mismatch-stereo-still-resolves-by-position`).

**2. FTP "From Kronos" dialogs truncated long error messages.** Both `SampleRemoteBrowserDialog`
(Sample Editor's browser) and `RemoteFilePickerDialog` (a byte-for-byte duplicate used by
the PCG source) had `TXT_Status` set to `TextTrimming="CharacterEllipsis"` with no wrap -
a `ConnectFailed`/`DownloadFailed` message (which interpolates the raw exception text)
routinely ran longer than the window's width, silently cutting off exactly the host/
path/errno detail that makes the error actionable. Switched both to `TextWrapping="Wrap"`;
the containing Grid already has the ListBox as its only flexible (`*`) row, so a taller
wrapped message just shrinks the list rather than clipping or resizing the window. Both
dialogs are now also wired into `--ui-theme-smoketest` (previously neither was - added a
behavioral check reading `TXT_Status.TextWrapping` directly, not just "constructs without
throwing"; negative-controlled). Since `Loaded` (where the real FTP connection kicks off)
never fires for a constructed-but-unshown window, adding them to the smoketest reaches
out over the network to nothing.

**3. Delete Zone permanently disabled once a zone was already skipped.** An empty
"(skipped) up to X" placeholder could never be cleared back out of the keymap - the
button just greyed out. `DeleteSelectedZone` is now two-stage: first delete on a real
zone soft-skips it (unchanged - the underlying .KSF stays on disk, the key range stays
reserved, no side effect on neighbors); second delete on an ALREADY-skipped zone
physically removes it from the multisample's `Zones` list via new `DeleteSkippedZone`,
mirrored onto the stereo sibling at the same index. No explicit range math needed - each
zone's low bound is derived from its predecessor's own TopKey + 1, so the following zone
automatically absorbs the vacated range once the entry is gone (the exact side effect the
soft-skip stage exists to avoid on a zone that might still matter; deliberate here since
there's nothing left worth reserving a placeholder for). `BtnDeleteZone`/`MNU_DeleteZone`
are enabled for ANY selected zone now (were `!ZoneIsSkipped`), with the button's own
content/tooltip switching between "Delete Zone" and "Remove Zone" so the state-dependent
action is visible rather than silently different.

- **Deliberately NOT Ctrl+Z-able**, matching `AddPlaceholderZone`'s own documented
  precedent: removing an entry changes the multisample's child count, which needs the
  SAME `RefreshTreeAfterMutation` rebuild every zone-ADDING method already uses (a
  boundary drag/reorder/key edit never changes count, which is the actual reason those
  three stay undoable) - and that rebuild's `SelectNode(null)` resets `_zoneUndo` on the
  resulting scope change, so an undo step recorded before the rebuild would be discarded
  before it could ever be used. Revert KSC Changes remains the available undo path, same
  as for every zone-adding method. My first draft DID try to wire this into `_zoneUndo`
  anyway; the new self-test caught the wipe immediately (`CanUndo` false right after the
  call), which is what led to matching the established precedent instead.
- **Found and fixed while building this**: `_zoneDirty = true`'s usual auto-registration
  (`RegisterDirtyMultisample()`, called via the property setter) resolves the owning
  multisample by walking `Zones.Contains(_selectedZone)` - but by the time that setter
  runs in `DeleteSkippedZone`, `_selectedZone` has ALREADY been removed from `m.Zones`,
  so that lookup fails and silently skips registering `m` at all. Fixed by bypassing the
  property setter (`_zoneDirtyField = true` directly) and calling
  `RegisterDirtyMultisample(m, kmpPath)` with the already-known reference instead.
- **Second bug found in the same area**: `SaveSelectedMultisample`'s own entry guard
  required an active tree selection ("No multisample selected") even though its BODY
  (per entry 21) writes every pending entry in `_dirtyMultisamples` regardless of
  selection - so after `RefreshTreeAfterMutation`'s rebuild clears selection, Save
  Multisample/Save Changes could no longer reach ANY pending edit, not just the one just
  removed. Widened the guard to only refuse when NOTHING is selected AND NOTHING is
  pending.
- Both fixes are independently negative-controlled: reverting either one in isolation
  fails `delete-zone-second-delete-persisted-removal` (`SamplePhase5SelfTests`, updated
  from its old "refuses a double-delete" assertion to the new remove-and-persist one)
  and/or the new `deleteskipped-removal-persisted-*` checks (`SamplePhase13SelfTests`
  block 13, which now saves-and-clears between its two deletes specifically so it doesn't
  rely on the FIRST delete's own registration accidentally covering the second - an
  earlier draft of this test passed even with the registration bug present, for exactly
  that masking reason, before this adjustment).

Verification: clean `dotnet build`, `--librarian-selftest` green (13 self-test blocks in
`SamplePhase13SelfTests.cs` now, plus `SamplePhase5SelfTests`'s updated delete-zone
block), `--ui-theme-smoketest` green (4 new checks: two dialog constructions, two status-
wrap behavioral checks), `--sample-editor-smoketest` green,
`--sample-format-fixture-check` still 75/75 byte-identical, `SampleFixtures/` confirmed
clean via `git status`. `--sample-editor-visual-check` re-run and read back directly -
confirms a freshly-added skipped placeholder zone shows an ENABLED "Remove Zone" button
(previously would have shown a greyed-out "Delete Zone").

**Not yet visually confirmed**: the stereo-view positional fallback (no real mismatched-
keymap fixture exists among the checked-in `SampleFixtures/`) and the FTP dialogs' wrapped
error text (needs an actual long connect-failure message against a real or deliberately-
wrong host, which the self-test's dummy-args construction doesn't exercise - it only
proves the DP is set correctly, not what a real multi-line error looks like on screen).

**25. Keymap piano doesn't render until the tab is manually clicked (2026-08-21,
follow-up to entry 24) - `EditorTabs.SelectedItem` could get permanently stuck at
`null`.**

**Root cause**: `RefreshDetailPanels`'s reselect-fallback (entry 21, added to stop a
collapsed tab showing blank content) pattern-matched `EditorTabs.SelectedItem is TabItem
{ Visibility: not Visibility.Visible }`. That match silently FAILS when `SelectedItem`
is already `null` - `null` doesn't match `TabItem {...}` at all, visible or not.

`SelectedItem` reaches `null` whenever a zone add/delete transiently collapses EVERY tab
at once: right after a tree-rebuild's own `SelectNode(null)`,
`HasZoneSelected`/`HasSampleLoaded`/`CurrentMultisampleZones` are ALL false/empty
simultaneously, so `TabKeymap`/`TabSamples`/`TabLooping` all go `Collapsed` in the SAME
`RefreshDetailPanels` call - the fallback's OWN `FirstOrDefault(t => t.Visibility ==
Visible)` then correctly finds nothing and sets `SelectedItem = null`. On the NEXT call,
once a tab becomes visible again (e.g. `AddPlaceholderZone`'s own re-select, or the user
picking a different tree node afterward), the SAME broken pattern match can never
recover from `null` - the pane stayed permanently blank until a manual tab click, which
is the ONLY other code path that ever sets `SelectedItem` directly.

This explains BOTH halves of the report: "especially when adding or deleting a zone"
(directly triggers the null-out) and "occasionally when selecting a KMP" (really
observing corruption LEFT OVER from an earlier add/delete in the same session -
`DeleteSkippedZone`'s TRUE removal is the more reliable repro of the two, since nothing
re-selects anything afterward at all, unlike Add Zone, which usually - but not always -
papers over it within the same call by re-selecting the new zone).

**Fix**: one-line - inverted the condition to `is not TabItem { Visibility:
Visibility.Visible }`, which catches both "collapsed TabItem" and "null" the same way.

**Confirming clue found while writing the regression test**: `Tools/
SampleEditorVisualCheck.cs`'s own Add Zone screenshot step had `((TabItem)win.EditorTabs.
Items[0]).IsSelected = true; // Keymap - show the new zone's bar/highlight` immediately
after the Add Zone click - a MANUAL workaround for exactly this bug, needed because the
app's own reselect logic couldn't recover on its own. Replaced with a log line reporting
what the app lands on unassisted (`EditorTabs.SelectedItem: Keymap`, confirmed via a
fresh screenshot) rather than continuing to paper over it.

**New regression check in `--ui-theme-smoketest`**: constructs a real, off-screen-but-
shown `SampleEditorWindow` (same technique the existing Librarian block already uses -
`Opacity=0`, moved off-screen, `ShowInTaskbar=false`) against a synthetic two-zone
fixture, selects zone 0, clicks Delete Zone TWICE (soft-skip then true-remove, driven via
real `ButtonBase.ClickEvent` RaiseEvent calls - not direct ViewModel calls) to force the
all-tabs-collapsed transient, then RE-SELECTS the surviving zone (simulating "selecting a
KMP" after the corruption) and asserts `EditorTabs.SelectedItem is TabItem { Visibility:
Visible }`. Negative-controlled: reverting the fix reproduces the exact reported failure
mode (`EditorTabs.SelectedItem is null after re-selecting a zone post-delete`).

**Real hang found and fixed while building the test itself, not a bug in the app**: the
test's own `editor.Close()` call blocked forever, because the two deletes left real
unsaved changes and `OnWindowClosing`'s "discard unsaved changes?" `MessageBox.Show` is
genuinely modal - with no user present in a headless test run, it waits for input that
never comes. Every checkpoint up through `recovered=True` printed instantly; only
`Close()` itself never returned. Fixed by clicking `BtnSaveChanges` (another real
`RaiseEvent`, not a VM shortcut) before closing - which also happens to exercise entry
24's `SaveSelectedMultisample` guard-widening fix for real, since nothing is selected at
that point either.

Verification: clean `dotnet build`, `--ui-theme-smoketest` green (29 checks, 0 failures,
including the new one), `--librarian-selftest` green, `--sample-editor-smoketest` green,
`--sample-format-fixture-check` still 75/75 byte-identical, `SampleFixtures/` confirmed
clean via `git status`. `--sample-editor-visual-check` re-run with the tab-forcing
workaround removed - screenshot confirms the Keymap tab (piano + zone bar) renders
correctly immediately after Add Zone with no manual tab click, and the console log
confirms `EditorTabs.SelectedItem: Keymap` was reached unassisted.

**26. First pass at a Kronos-hardware-like layout for the Multisample Editor pane
(2026-08-21).** Request: move the piano keymap out of the tab strip to a static spot
above the waveform, rename the "Zone" header to "Multisample Editor", and give it the
Kronos's own two-section shape - an MS (multisample) picker, then an Index/Sample/
Orig.Key/Top Key/Range row with Create/Delete. Explicitly a starting point ("adjust as
we go"), not a final layout.

- **Keymap is now static**, pulled out of the removed `TabKeymap` `TabItem` into its own
  `KeymapSection` `Border` at the very top of the scrollable pane (above the "Multisample
  Editor" header) - visible whenever the selected multisample has any zones, regardless
  of which of the two remaining tabs (Samples/Looping) is active. `EditorTabs.Visibility`
  no longer factors in Keymap visibility (there's nothing left in the tab strip to hide
  for that reason); it now tracks `HasSampleLoaded` alone, same as `TabSamples`/`TabLooping`
  already did.
- **New "MULTISAMPLE (MS)" section**: a `ComboBox` (`MultisampleCombo`) listing every
  multisample node across every open collection
  (`SampleEditorViewModel.AllMultisampleNodes()`, a thin `EnumerateNodes(Roots).Where
  (MultisampleRef != null)`). Picking one is routed through the SAME `SelectTreeNode`
  helper `OnKeymapZoneClicked` already used for the keymap - drives the real
  `TreeViewItem.IsSelected`, so it's genuinely "the same as selecting the .KMP in the
  tree," not a parallel code path. Synced to the tree's current selection by reference
  equality between `CurrentMultisampleZones` and a candidate node's
  `MultisampleRef.Multisample.Zones` (`SelectNode` hands out that exact list reference).
- **Zone panel reworked**: `Filename:` (plain text) replaced by `Sample:` (a `ComboBox`,
  `ZoneSampleCombo`, listing every zone's filename in the current multisample -
  `"(skipped)"` for a skipped one - same select-via-`SelectTreeNode` pattern as MS).
  Added `Index:` (editable `TextBox`, 1-based, LostFocus-commits, clamps to
  `[1, zone count]` same as every other field in this window) and a read-only `Range:`
  computed the same way `KmpZone.TopKey`'s own doc comment defines a zone's range -
  `(previous zone's TopKey + 1)` through `this zone's own TopKey`, `0` (not the previous
  zone) for index 0. `Add Zone`/`Delete Zone` renamed `Create`/`Delete` (both the static
  XAML label and the two-stage skip-then-remove dynamic Content, entry 24's
  `"Remove Zone"` shortened to `"Remove"` to match) - the two-stage behavior itself and
  its ToolTip switching are UNCHANGED. `Import Sample...` kept.
- **Follow-up (same day, same round)**: header moved to the very top of the pane (above
  the keymap, not below it as first placed) since "at the top" meant the pane's own top,
  not just above the waveform; `KEYBOARD` section label dropped (a piano keyboard doesn't
  need one); `Create`/`Delete`/`Import Sample...` moved onto their own `WrapPanel` row
  below the Index/Sample/Orig.Key/Top Key/Range row, inside the same `ZonePanel` Border
  (was previously one WrapPanel that could wrap the buttons onto that row's tail
  end depending on window width - now two, so the action row is fixed regardless of
  width).
- Both combos guard against feedback loops with a single `_suppressComboEvents` bool set
  around `RefreshDetailPanels`' own `ItemsSource`/`SelectedItem` writes - same shape as
  every other re-entrancy guard already in this file, not a new pattern.
- **Placement call made without asking**: "move...up to the top" was read as literally
  the top of the whole pane (above the MS/Index sections too), matching the Kronos's own
  Sample/Multisample Edit page, where the keyboard graphic is a permanent fixture above
  everything else. Flagged here in case that reads wrong once seen live - trivial to
  reorder.

**Pre-existing test failure found during verification, NOT caused by this round**:
`--ui-theme-smoketest`'s entry-25 regression check ("SampleEditorWindow Keymap tab
recovers after zone delete") now fails (`EditorTabs.SelectedItem is null after
re-selecting a zone post-delete`) even with every file from this round's own edit set
(`Views/SampleEditorWindow.xaml`, `.xaml.cs`, `ViewModels/SampleEditorViewModel.cs`)
stashed back out via `git stash push -- <those 3 files>` and the test re-run against the
untouched baseline. Confirmed via `--sample-editor-visual-check`'s Add Zone step too
(`EditorTabs.SelectedItem: (none - regression)`), independent of the smoketest. Given
entry 25's own log claimed this exact check green, something in the working tree since
then (the InsertSilenceDialog/FTP-dialog-wrap work also sitting uncommitted alongside
this round - see this file's own entries above 26 - is the likeliest culprit, but wasn't
investigated) reintroduced it. Left alone rather than guessed at, per this repo's own
standing rule against fixing something not actually understood yet.

Verification: clean `dotnet build`, `--librarian-selftest` green, `--sample-format-
fixture-check` 75/75 byte-identical, `--sample-editor-smoketest` (VM-level, no window)
fully green, `--sample-editor-visual-check` screenshots confirm the new layout (Keyboard
section, Multisample Editor header, MS dropdown, Index/Sample/Orig.Key/Top Key/Range/
Create/Delete/Import row) renders correctly both with and without a zone selected,
`SampleFixtures/` confirmed clean via `git status`. `--ui-theme-smoketest` has the ONE
pre-existing failure above; every other check in it (29 total) still passes. Owed:
real click-through in the live app - this was verified only via the headless visual-
check screenshots and self-tests, not by running the app interactively.

**27. Second, larger pass on the Multisample Editor pane (2026-08-21) - empty state,
reorder, auto-drill-in, sample names, rename, stereo VU, and a new Settings tab.**

- **Empty state**: `EmptyStateText` ("Import a .KSC file...") is now the ONLY thing the
  pane shows when `Roots.Count == 0` - everything else (header, MS picker, keymap, zone
  detail, waveform/tabs) moved under one new `EditorContent` `StackPanel`, collapsed as a
  whole rather than each section separately gating itself on "is anything loaded."
  `RefreshDetailPanels` now returns right after that toggle when nothing's loaded -
  nothing below it has a meaningful state to set anyway.
- **MS section moved above the keymap** (was below it) - it answers "which multisample"
  before "which key range within it."
- **Index/Sample panel no longer needs a zone specifically selected** - `ZonePanel`'s
  gate changed from `HasZoneSelected` to the same `CurrentMultisampleZones is {Count:>0}`
  condition the keymap already used, AND `OnTreeSelectionChanged` now auto-drills into
  `node.Children[0]` whenever the newly selected node is a multisample with any zones -
  selecting a multisample (tree, MS dropdown, or keymap - all three ultimately select a
  tree node) lands on zone 1 immediately, matching the Kronos's own behavior, so the
  panel is populated, not just visible-but-blank, the instant a .KMP is picked. A
  multisample with zero zones still shows the (now-blank) panel rather than hiding it.
- **Sample dropdown shows the sample's real Name (from inside its .KSF), not its
  filename** - the filename stays in the tree on the left, unchanged. Reading a Name
  means opening the .KSF, which is too expensive to redo on every `RefreshDetailPanels`
  call (fires on nearly every edit), so it's cached per session in a new
  `_sampleNameCache` (ksfPath → name) rather than re-read every time; cleared wherever a
  zone's .KSF content could actually change (Import Sample into Zone, Rename Sample).
  **Not fully addressed**: a multisample with a large zone count could still pay a real
  one-time disk-read cost the first time it's selected (one small file read per zone);
  left as-is given typical zone counts, flagged rather than built out further (async
  loading, etc.) without being asked for it.
- **Edit > Rename Multisample.../Rename Sample...** (`SampleEditorViewModel.
  RenameSelectedMultisample`/`RenameSelectedSample`, new `PromptDialog`-driven handlers)
  - renames only the `Name` field stored inside the .KMP/.KSF (Suffix, the "-L"/"-R"
  stereo marker, is left alone - editing it here would silently break stereo pairing,
  which matches by exact Suffix). Mirrored onto a resolved stereo sibling/partner the
  same way every other zone/name edit here already is (`FindLiveStereoSibling` for the
  multisample, `ShouldMirrorToPartner`/`_partnerSample` for the sample) - renaming only
  one half would otherwise silently break the pairing, the same bug class entry 19 fixed
  for Add Zone. A live edit like every other field in this window: marks the file dirty
  via the existing `RegisterDirtyMultisample`/`_sampleDirty` mechanisms, doesn't save
  immediately - Save Changes/Save Multisample/Save Sample writes it. Menu items enabled
  via `CurrentMultisampleName`/`HasSampleLoaded`.
- **VU meter is stereo now**: `SamplePlayback` exposes `PeakLevelLeft`/`PeakLevelRight`
  alongside the existing combined `PeakLevel` - `MeteringSampleProvider.MaxSampleValues`
  was ALREADY per-channel (index 0/1), the old code just collapsed it with `.Max()`.
  `SampleVuMeterControl` gained a `ShowLabels` DP (default true) so two can sit side by
  side in the same horizontal space the old single meter used - the left one
  (`VuMeterLeft`, `ShowLabels="False"`) is a bare bar, the right one
  (`VuMeterRight`) keeps the dB tick labels both used to draw independently. `VuMeterLeft`
  is collapsed for a mono sample (`HasStereoPair`-gated, same pattern as `SplitLRBox`);
  the outer column widened 70→92px to fit the extra bar.
- **New Settings > Sample Editor tab**: "Create Zone Preferences" - Position
  (Right/Left), Zone Range (1-127, replaces the old hardcoded 12-key cap), Original Key
  Position (Bottom/Center/Top). Persisted as three new `AppSettings` properties (`Clone()`
  picks them up automatically via its existing reflection loop, no change needed there).
  **Wired into `AddPlaceholderZone`, not just stored**: Position Right is the ORIGINAL
  behavior unchanged (new zone takes the top of the carved range, existing last zone
  shrinks); Position Left is new - new zone takes the BOTTOM of the carved range instead,
  the existing last zone keeps its own Top Key unchanged, and the new zone is `Insert`ed
  just before it (not `Add`ed at the end) so list order still matches the ascending-key-
  order the TopKey range convention assumes (KmpZone.TopKey's own doc comment). Original
  Key Position picks where OriginalKey (independent of the trigger range TopKey defines)
  lands in the new zone: Bottom = the range's low end (old default), Center = midpoint,
  Top = the range's high end. Mirrored onto a resolved stereo sibling with the same
  `Insert`-at-the-same-index logic. **Design call made without being asked to justify
  it**: "Position" and "Original Key Position" are both genuinely ambiguous specs with no
  single obvious reading - this is A reasonable, symmetric interpretation (literal
  left/right on the piano, literal low/mid/high within the new range), not verified
  against any specific expected Kronos behavior. Flag if it doesn't match what was
  actually wanted; the settings themselves and their persistence are solid regardless of
  whether the algorithm they drive needs adjusting.
- **`AddPlaceholderZone`'s return-value plumbing changed to survive the Left position**:
  the old code assumed the new zone was always the target multisample's LAST child after
  the rebuild (`Children[^1]`) - true only for Position Right. New zone position is now
  tracked via `LastAddedZoneIndex` (a side-channel property set right before the rebuild,
  since the rebuild replaces every `KmpZone` with a fresh instance - no reference survives
  it, only a position can) rather than changing `AddPlaceholderZone`'s own return type,
  which would have broken several existing call sites
  (`SamplePhase12SelfTests`/`SamplePhase13SelfTests`/`SampleEditorSmokeTest`) that treat
  it as a plain `string?`.

Verification: clean `dotnet build`, `--librarian-selftest` green (including the
Phase12/13 self-tests that call `AddPlaceholderZone` - confirms the Position-Right default
path still behaves identically), `--sample-format-fixture-check` 75/75 byte-identical,
`--sample-editor-smoketest` fully green (placeholder-zone add still reports the same
"C#4-C5" range as before - Right-position/default-Range-12 output unchanged),
`--sample-editor-visual-check` screenshots confirm: empty state shows only the import
prompt; selecting a multisample shows MS-above-keyboard with the zone panel already
populated (Sample combo showing "around_the_world_vox", not "MS000000.KSF"); Create Zone
still produces the same key range as before. `SampleFixtures/` confirmed clean via `git
status`. `--ui-theme-smoketest` has the same ONE pre-existing failure documented under
entry 26 (confirmed unrelated to this round too - it already reproduced on a clean
baseline before entry 26's edits, and nothing this round touches the code path involved);
every other check still passes. **Not verified**: Rename Multisample/Rename Sample and
the new Settings tab's actual save/reload round-trip - no automated coverage was added
for either (would mean extending `Tools/SampleEditorSmokeTest.cs` and/or
`UiThemeSmokeTest.cs`, out of scope for this pass), and neither was click-tested in the
live app. Stereo VU similarly unverified beyond "it compiles and the layout renders" -
confirming real L/R needle movement needs actual stereo playback, which the headless
visual-check doesn't exercise.

**28. VU meter clarity fix, Split L/R channel picker, editing frame restructure, icon
buttons (2026-08-21, follow-up to entry 27).**

- **Stereo VU wasn't actually broken, just illegible as stereo.** Root cause: the two
  meters were sized/styled asymmetrically (`VuMeterLeft` 14px bare bar, `VuMeterRight`
  ~26px WITH dB tick labels) plus a separate `VolumeControl` fader further right - three
  elements, but visually read as "one narrow accent stripe + one real meter + a slider,"
  not "a matched L/R pair." **Fix**: both bars are now equal width, neither draws its own
  dB tick numbers any more (`ShowLabels="False"` on both - there was never room for tick
  text on a sidebar this narrow once split two ways), and each gets a small "L"/"R"
  caption directly underneath. `VolumeControl` (the actual playback-volume fader) is
  unchanged and is the genuinely "tiny bar" - confirmed correctly separate via the
  `05b_real_zone_selected_scrolled.png` visual-check screenshot, which shows both
  captioned bars plus the volume fader as three distinct elements.
- **Split L/R channel picker** (`SplitChannelCombo`, next to `SplitLRBox`, shown only
  when both `HasStereoPair` and `SplitLR`): Split mode's own code comment used to say
  outright "select the OTHER channel's zone in the tree to edit it" - there was no
  in-window way to reach the R channel at all once split, exactly the gap reported.
  Picking L/R now selects that side's zone the same way the MS/Sample dropdowns already
  do - `SelectTreeNode` via a real `TreeViewItem`, so `OnTreeSelectionChanged` fires
  normally. Needed one new public VM surface: `SampleEditorViewModel.PartnerZoneRef`
  (the resolved stereo partner's own zone+path, previously private-only) so the window
  can locate its tree node via the existing `FindNodeForZone` helper.
- **Editing frame**: the waveform/transport area is now wrapped in a `Border`
  (`EditingFrame`, same `FieldSection` style every other section already uses) instead of
  floating with no visual boundary. Transport (Locate/Rewind/Play/Pause/FF/Locate)+Zoom+
  Undo/Redo moved from BELOW the waveform to ABOVE it, inside the same frame - a static
  toolbar, unchanged by which tab is active. The `Samples`/`Looping` `TabControl` moved
  to live INSIDE this same frame too (was a sibling section below it before), and the DSP
  editing buttons (Crop to Selection/Normalize/Trim Silence/Fade In/Fade Out/Reverse/
  Silence) moved OUT of the static toolbar and INTO a new "EDIT" section inside the
  `Samples` tab, alongside the existing PLAYBACK FORMAT/REPAIR sections - swapping tabs
  now genuinely changes which editing buttons are available (Looping's own fields were
  already tab-scoped; DSP editing wasn't, until now). The waveform/ruler/scrollbar/VU
  itself stays exactly where it was, static, unaffected by either tab - confirmed via the
  same scrolled screenshot showing the waveform sitting above an intact tab strip whose
  content (PLAYBACK FORMAT/EDIT/REPAIR) is fully visible below it, all inside one
  unbroken border.
- **Zoom In/Out are icon-only now** (a magnifying glass with a +/- inside, built from
  `Ellipse`+`Line` primitives at a small fixed size rather than a single Path - simpler to
  get right without a rendering preview than freehand arc geometry) - Zoom to
  Selection/Fit keep their text labels, since the request was specifically about In/Out.
- **Undo/Redo are icon-only circular arrows** (CCW for Undo, CW for Redo, tooltip carries
  the word since the icon alone can't distinguish direction at this size) - `Redo`'s path
  data is a literal horizontal mirror of `Undo`'s (same arc radius/center, mirrored
  x-coordinates, sweep flag flipped) rather than independently derived, so the pair is
  guaranteed to read as a matched set even though the exact arc angles were hand-derived
  without a rendering preview and are approximate, not pixel-verified against a reference
  icon.
- All four new icon buttons reuse the existing `SampleTransportBtn` style (same
  hover/press/disabled chrome as the transport row) rather than inventing a new button
  look, matching this window's own established "reuse, don't redraw" precedent
  (`SampleTransportBtn`'s own comment, entry-19-era) for the transport icons themselves.

Verification: clean `dotnet build`, `--librarian-selftest` green, `--sample-format-
fixture-check` 75/75 byte-identical, `--sample-editor-smoketest` fully green,
`--sample-editor-visual-check` screenshots confirm the bordered editing frame, the
toolbar-above-waveform reorder, the tab-scoped EDIT section, and the captioned stereo VU
bars all render as intended. `SampleFixtures/` confirmed clean via `git status`.
`--ui-theme-smoketest` has the same ONE pre-existing failure from entry 26 (still
unrelated - nothing this round touches `RefreshDetailPanels`' tab-reselect fallback);
every other check still passes. **Not verified**: the Zoom In/Out and Undo/Redo icons'
exact visual quality at real screen DPI/scaling - confirmed only via a screenshot at the
visual-check's fixed window size, not click-tested or inspected at native resolution in
the live app. Split L/R channel picker confirmed by code/pattern review (identical to the
already-working MS/Sample combo pattern) but not click-tested live either - the
visual-check fixture's default view is Combine mode, not Split, so it never exercises
this specific control.

**29. Tabs removed entirely - Samples/Looping flattened into rows (2026-08-21, follow-up
to entry 28).** Request: fold Playback Format into the toolbar/Name row, turn EDIT and
REPAIR into their own rows between the toolbar and the Name/Frames row, move Loop Enabled
+ its detail fields (shown only once enabled) between Name/Frames/Sample Rate and the
waveform, rename "Loop = Selection" to "Loop Selected". Once every tab's content had
somewhere else to live, nothing was left to put IN a tab - so the `Samples`/`Looping`
`TabControl` itself is gone, not just reorganized. Final row order inside `EditingFrame`,
top to bottom: transport/zoom/undo-redo/**Tempo x/Pitch/Apply Tempo-Pitch** toolbar → EDIT
row (Crop/Normalize/Trim Silence/Fade In/Fade Out/Reverse/Silence) → REPAIR row (Remove DC
Offset/Insert Silence) → Name/Frames/**Sample Rate** row → Loop row (Enabled toggle,
same-row detail fields) → waveform (unchanged, static, last).

- **Sample Rate** moved from the old PLAYBACK FORMAT section into the Name/Frames row,
  same `ReadOnlyFieldBox` control (already styled flat/non-interactive, no conversion to
  a plain `TextBlock` needed - it already reads as "information," not an editable field).
- **Loop detail fields collapse as one group** (`LoopFieldsRow`, a `WrapPanel` nested
  inside the outer Loop row's `WrapPanel`) until `LoopEnabledBox` is checked - new
  behavior, these used to be always-visible in the Looping tab regardless of the toggle.
  `RefreshDetailPanels` sets `LoopFieldsRow.Visibility` from `_vm.SampleLoopEnabled`
  alongside where it already sets `LoopEnabledBox.IsChecked`, no VM change needed.
- **`BtnLoopFromSelection` renamed** "Loop = Selection" → "Loop Selected" (`Content` only,
  handler/behavior unchanged).
- **`RefreshDetailPanels` lost the entire TabControl chrome-management block**: the
  `TabSamples`/`TabLooping`/`SamplePanel`/`LoopingPanel`/`EditorTabs` visibility lines, and
  the reselect-fallback (`if (EditorTabs.SelectedItem is not TabItem {...})`) that entries
  21/25/26/28 each touched in turn chasing the same recurring regression - all deleted
  together with the `TabControl` itself, since there's no `SelectedItem` left to manage.
- **The entry-26/27/28 pre-existing `--ui-theme-smoketest` failure ("Keymap tab recovers
  after zone delete") is GONE, not just still-unrelated** - it was pinning exactly the
  `TabControl` reselect-fallback bug class the paragraph above describes, and that code
  path no longer exists for the bug to occur in. Removed the whole test block (not
  rewritten to test something else) rather than leaving it permanently red or deleting it
  silently - see the removal's own comment in `Tools/UiThemeSmokeTest.cs` for why.
  `Tools/SampleEditorVisualCheck.cs`'s three-tab screenshot block (`05c_keymap_tab`/
  `05e_samples_tab_scrolled`/`05f_samples_tab_top`/`05d_looping_tab`) and its
  `EditorTabs.SelectedItem` logging line after Add Zone were removed the same way, for the
  same reason - `win.EditorTabs` no longer exists to reference.
- The `Border` wrapping `WaveformPanel` (`EditingFrame`) needed one structural fix along
  the way: it used to wrap an extra `StackPanel` that held `WaveformPanel` and
  `EditorTabs` as two siblings (a `Border` can only ever have ONE child) - with
  `EditorTabs` gone, that wrapper was redundant and came out too, so `EditingFrame`'s
  only child is `WaveformPanel` directly again.

Verification: clean `dotnet build`. `--ui-theme-smoketest` is fully green now - **28
checks, 0 failures**, the entry-26 pre-existing failure is gone along with the code it
was pinning, not papered over. `--librarian-selftest` green, `--sample-format-fixture-
check` 75/75 byte-identical, `--sample-editor-smoketest` fully green (Add Zone/stereo
pair/tempo-pitch/etc. all still report identical results to every prior entry - nothing
about the underlying edit operations changed, only where their buttons live).
`--sample-editor-visual-check` screenshots confirm the exact requested row order end to
end (toolbar-with-Tempo/Pitch, EDIT row, REPAIR row, Name/Frames/Sample Rate, unchecked
Loop Enabled with its detail fields correctly hidden, waveform last) and that the whole
frame is still visibly bordered. `SampleFixtures/` confirmed clean via `git status`.
**Not verified**: Loop Enabled actually checked (the detail-fields-appear behavior) -
the visual-check fixture never checks the box, so `LoopFieldsRow` going from Collapsed to
Visible was confirmed by code review (mirrors the exact same pattern `ZonePanel`/
`KeymapSection`/`SplitChannelCombo` already use successfully) and the Collapsed/hidden
half of the behavior, not screenshotted in its Visible state.

**30. Can't crop-select when the loop spans the whole sample (2026-08-21).** Reported:
"when the entire waveform is looped, there's no ability to click-drag-highlight."

**Root cause** (`Views/SampleWaveformControl.cs`, `OnMouseLeftButtonDown`): a click
starting inside `[LoopStartFrame, LoopEndFrame)` (but not on either edge) always grabbed
the whole region for a MOVE drag instead of starting a new crop selection there - by
design, so you can drag the loop region around. But `OnMouseMove`'s move-drag clamps the
new start to `[0, FrameCount - len]`; once the loop's own length (`len`) reaches the full
`FrameCount`, that range collapses to exactly `[0, 0]` - the region can never actually
move. With a whole-sample loop, EVERY click anywhere in the waveform falls inside it, so
every click was being swallowed by a drag that could never do anything, leaving no way to
crop-select at all.

**Fix**: the loop-region-grab branch now also requires `LoopEndFrame - LoopStartFrame <
FrameCount` - once the loop already spans the entire sample, a click/drag there falls
through to the normal crop-selection path instead, matching what dragging already does
everywhere else in the waveform. Same condition added to the hover-cursor logic (no more
misleading grab-hand cursor over a region that can't be dragged). Partial-length loops are
completely unaffected - drag-to-move-the-loop still works exactly as before there, and the
loop edges themselves (drag one boundary independently) were never affected either way,
since those are separate `NearPixel` checks earlier in the same method.

Verification: clean `dotnet build`, `--librarian-selftest` green, `--sample-format-
fixture-check` 75/75 byte-identical, `--sample-editor-smoketest` fully green,
`--ui-theme-smoketest` fully green (28/28, still - this fix doesn't touch any window
construction path). **Not verified live**: this is a pure mouse-drag interaction fix with
no layout change to screenshot - confirmed by code/logic review (the clamp math above)
and by the fact every existing self-test/smoketest still passes unchanged, but not
click-tested by actually dragging inside a whole-sample loop in the running app.

**31. Whole-region loop drag gated on Loop Lock; click-to-select-green removed
(2026-08-21, follow-up to entry 30).** Request: dragging the loop region as a whole
should require Loop Lock to be on (off otherwise falls through to plain crop-selection,
loop region included); the single-click "turns the region green" toggle is no longer
needed now that crop-selection highlighting works everywhere.

- **New `SampleWaveformControl.LoopLockEnabled` DP**, mirrored from `_vm.LoopLockEnabled`
  in `RefreshDetailPanels` at all three places `LoopEnabled` was already being set
  (Combine's `foreach` over both panes, Split, mono) - the same plumbing pattern, nothing
  new invented.
- **`OnMouseLeftButtonDown`'s loop-region-grab now requires `LoopLockEnabled`** (on top of
  entry 30's own "loop doesn't already span the whole sample" condition) - with Loop Lock
  off, a click/drag starting inside the loop body falls straight through to the normal
  crop-selection path, exactly like clicking anywhere else on the waveform. The hover
  cursor (grab hand vs none) picked up the same condition, so it stops promising a drag
  that Loop Lock being off no longer allows.
- **`LoopSelected` removed entirely** (DP, `LoopSelectedChanged` event, every set/clear
  site, the stereo-pane mirroring handler in `SampleEditorWindow.xaml.cs`, the XAML
  wiring) rather than merely disconnecting the click that used to set it - once the click
  toggle is gone, nothing else ever sets it true, so it was fully dead weight, not code
  worth leaving dormant "in case." Its two former jobs were re-homed onto the same
  `LoopLockEnabled` gate the drag now uses, rather than dropped outright:
  - **Loop region fill color**: green while `LoopLockEnabled` (draggable as a whole right
    now), faint blue otherwise - was green while `LoopSelected` (clicked), faint blue
    otherwise. Same visual language, now tied to an actual mode toggle instead of a
    per-click state that had no persistent meaning.
  - **Arrow-key nudge** (`OnKeyDown`, Left/Right moves the whole region by one frame):
    gated on `LoopLockEnabled && HasLoop` instead of `LoopSelected && HasLoop` - dragging
    and nudging are the same underlying action (reposition the loop), so they now share
    one gate instead of two different ones.
  - **Plain click with no movement inside a Loop-Locked region** (`_draggingLoop` true,
    `_loopDragMoved` false in `OnMouseLeftButtonUp`): used to toggle `LoopSelected`; now
    falls back to the same "play from here" scrub-click every other plain click on the
    waveform already produces (`ScrubFrame`/`ScrubRequested`), rather than doing something
    loop-region-specific.
- `Themes/Dark.xaml`'s `WaveformLoopSelectedBrush` comment updated to describe what it
  now actually means (Loop Lock on, not "clicked") - the brush itself (and its key name)
  is unchanged, only the color's meaning shifted.

Verification: clean `dotnet build`, `--librarian-selftest` green, `--sample-format-
fixture-check` 75/75 byte-identical, `--sample-editor-smoketest` fully green,
`--ui-theme-smoketest` fully green (28/28). Confirmed no `LoopSelected` references survive
anywhere in the repo (`grep -rl` across `.cs`/`.xaml`, excluding the unrelated "Loop
Selected" BUTTON text/handler, which is a different feature - setting the loop from the
current waveform selection - untouched by this entry). **Not verified live**: same
category as entry 30 - a mouse/keyboard interaction change with nothing new to
screenshot; confirmed by code review and the full self-test suite staying green, not by
actually toggling Loop Lock and dragging in the running app.

**32. Tree simplified to just the loaded library; Loop Lock's own 1-click UI delay fixed
(2026-08-22).**

**Loop Lock delay**: `OnLoopLockChanged` set `_vm.LoopLockEnabled` but never called
`RefreshDetailPanels()` (unlike its sibling `OnLoopEnabledChanged`), and pushing
`LoopLockEnabled` down onto `WaveformLeft`/`WaveformRight` (the drag-gate DP entry 31
added) only happens inside that method - so checking the box left the waveform running
on the STALE value for one more click, until whatever that next click happened to do
called `RefreshDetailPanels` anyway. Fixed by adding the same `RefreshDetailPanels()`/
`UpdateStatus()` pair every other checkbox handler here already has.

**Tree simplified to root-only** ("now that the majority of the control is handled by
the sample editor... we don't need to show everything, just the parent"): `SampleTree`
gained an explicit `ItemTemplate` (a plain `DataTemplate`, no `ItemsSource`) that
overrides the `HierarchicalDataTemplate` in `Window.Resources` at the top level only -
multisample/zone nodes never get a `TreeViewItem` any more, so the tree is just a flat
list of loaded libraries (`.KSC` roots). `SampleTreeNode.Children` and the rest of the
data model are UNCHANGED - `AllMultisampleNodes`, `FindMultisampleContaining`, etc. all
still walk the full hierarchy in memory; only the TREE UI stopped rendering it.

- **`SelectTreeNode` rewritten** (still the one function every combo/keymap/Add-Zone
  reselect path calls, same signature, same call sites - zero changes needed at any of
  the six call sites): no more `BuildPath` + per-level `IsExpanded`/`ContainerFromItem`
  walk down to the target. It now just calls `_vm.SelectNode(target)` +
  `RefreshDetailPanels()`/`UpdateStatus()` directly, and separately looks up ONLY the
  owning ROOT's container (via `BuildPath`, `path[0]`) to keep it highlighted in the
  tree - there's nothing deeper left to expand or reveal.
  - **Re-entrancy trap avoided**: setting the root's `TreeViewItem.IsSelected = true`
    still fires `TreeView.SelectedItemChanged` (→ `OnTreeSelectionChanged` →
    `_vm.SelectNode(root)`), which would clobber the real zone/multisample selection
    `SelectTreeNode` had just made, the instant navigation actually crosses into a
    DIFFERENT library. New `_suppressTreeSelectionEvent` guard (same shape as the
    existing `_suppressComboEvents`) wraps that one write.
- **`OnTreeSelectionChanged` lost its auto-drill-into-first-zone branch** - dead code
  now, since a genuine user tree click can only ever land on a root (no MultisampleRef
  node is reachable through the tree UI any more); the same drill-in logic still lives
  in `SelectTreeNode` itself, for the one path that CAN select a multisample (the MS
  dropdown).
- **`Tools/SampleEditorVisualCheck.cs` updated to navigate the same way a real user
  now does** - drives `MultisampleCombo`/`ZoneSampleCombo` `SelectedIndex` instead of
  drilling into now-nonexistent multisample/zone `TreeViewItem` containers (which would
  silently return null everywhere past the root and leave every later screenshot showing
  nothing selected). `Tools/UiThemeSmokeTest.cs`'s own `ExpandAll` helper is unrelated -
  its only call site is the Librarian's local-library tree, a completely different
  `TreeView`.

Verification: clean `dotnet build`, `--librarian-selftest` green, `--sample-format-
fixture-check` 75/75 byte-identical, `--sample-editor-smoketest` fully green,
`--ui-theme-smoketest` fully green (28/28). `--sample-editor-visual-check` re-run
end-to-end: the tree screenshots now show a single flat "samplesfeb28_25.KSC" row with
no children at any zoom level, the MS dropdown/Index/Sample fields populate correctly
after selecting it, Add Zone still ends with the new (skipped) zone showing selected in
the Sample dropdown (confirmed via the tool's own console log, adapted from reading
`EditorTabs.SelectedItem`/tree state to reading `ZoneSampleCombo.SelectedItem`).
`SampleFixtures/` confirmed clean via `git status`. **Not verified live**: the Loop Lock
delay fix specifically - confirmed by code review (the missing call is now present,
matching the working sibling handler exactly) and the self-test suite staying green, not
by actually checking the box and dragging in the running app.

**33. Loading a library left nothing selected until a manual tree click (2026-08-22,
follow-up to entry 32).** Request: the first tree entry should become active as soon as a
new library loads. Root cause: `OpenCollectionPath`/`OpenKmpPath` (and the FTP pull
handlers, `OnPullCollectionFromKronos`/`OnPullMultisampleFromKronos`) never selected
anything after loading - `RefreshDetailPanels` only ever ran off a real selection change,
and nothing here ever produced one, so the MS dropdown/keymap/zone panel all stayed blank
until the user clicked the new entry by hand. Sharper now that entry 32 made the tree
root-only: with nothing ever auto-selected, that one root wasn't even highlighted after
loading, despite being the ONLY thing left to click.

**Fix**: new `SelectFirstRoot()` helper (`if (_vm.Roots.Count > 0) SelectTreeNode
(_vm.Roots[0]);`) called after every "bring a library into the tree" entry point -
`OpenCollectionPath`, `OpenKmpPath`, `OnPullCollectionFromKronos`,
`OnPullMultisampleFromKronos`. Reuses `SelectTreeNode` itself rather than a separate
mechanism, so it gets the exact same selection/highlight/refresh path (and entry 32's
`_suppressTreeSelectionEvent` re-entrancy guard) every other selection in this window
already goes through - no new code path to keep in sync. Deliberately `Roots[0]`
specifically, matching the request's own wording ("the first entry in the tree list") -
opening a SECOND collection while one's already loaded still selects index 0, not
necessarily the one just added (Roots is append-only, per RebuildTreeFromCollection's own
doc comment), which is fine for the common single-collection-at-a-time case this was
actually reported against. Deliberately NOT touched: `OnNewCollection`/`OnNewMultisample`
(creating a fresh EMPTY collection/multisample) - "loading" a library and "creating" one
from scratch are different actions, and an empty new collection has nothing for
auto-selection to usefully populate anyway.

Verification: clean `dotnet build`, `--librarian-selftest` green, `--sample-format-
fixture-check` 75/75 byte-identical, `--sample-editor-smoketest` fully green,
`--ui-theme-smoketest` fully green (28/28). `--sample-editor-visual-check`'s own
`02_collection_loaded` screenshot - taken immediately after `OpenCollectionPath`, before
any other interaction - now shows "samplesfeb28_25.KSC" already highlighted in the tree
(previously nothing was selected at that point at all). `SampleFixtures/` confirmed clean
via `git status`. **Not verified live**: the FTP-pull entry points
(`OnPullCollectionFromKronos`/`OnPullMultisampleFromKronos`) - confirmed by code review
only (same one-line `SelectFirstRoot()` addition as the two paths that WERE
screenshotted), since this sandbox's live network reaches a different test Kronos than
the user's real hardware (see kronos-sample-editor memory) and pulling isn't exercised by
any headless test.
