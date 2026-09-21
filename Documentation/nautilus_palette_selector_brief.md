# Nautilus palette-selector brief

Brief for building a live palette-color-override selector in the Windows
KronosScreenRemote client — what's already there, what's confirmed-correct,
what to avoid, after a full day of Nautilus palette reverse-engineering
(2026-09-19).

The goal: a UI in this client that lets the user pick a palette index (e.g.
via an eyedropper on the mirrored screen) and override its displayed RGB on
the fly, to work around wrong colors on Nautilus. This is a CLIENT-SIDE
rendering fix, not a device-side fix — it does not and cannot change what
color the physical Nautilus LCD itself shows, only what the remote-view
render shows. Don't conflate the two; see "what this can't do" below.

## The mechanism already exists — build on it, don't reinvent it

The client already has a per-index override system wired into the render
path, just no UI to drive it yet:

- `Views/MainWindow.Streaming.cs:335-347` `RebuildLut()` — builds a 256-entry
  LUT once per settings/override change: `_overrides.TryGetValue(i, out var
  ov) ? ov : _basePal[i]`, then applies the *global* brightness/contrast/
  gamma/saturation curve (`Rendering/ImageAdjust.cs`) on top of every entry.
  `ApplyLut()` (:353+) is the actual per-pixel blit, a single `lut[frame[i]]`
  lookup — fast, no per-frame cost from adding overrides.
- `_basePal` (`Views/MainWindow.xaml.cs:31`) is the daemon's wire palette
  from the `KSCR` handshake (`Networking/StreamReceiver.cs:136-146`,
  `Views/MainWindow.Streaming.cs:61`) — confirmed this session (independent
  fresh investigation, not assumed) to be the *only* palette source the
  client trusts; no hardcoded/competing table anywhere in the C# project.
- `_overrides` (`Views/MainWindow.xaml.cs:40`) is a
  `Dictionary<int,PaletteEntry>`, loaded via `Storage.LoadOverrides()`
  (`Core/Storage.cs:196`) at startup (`:237`).
- `PaletteEntry` = `record struct PaletteEntry(byte R, byte G, byte B)`
  (`Core/Models.cs:62`).
- Override file: `{DataDir}/palette_override.json` where `DataDir` = the
  running exe's own directory (`Storage.cs:11`, NOT %AppData%). Format:
  `{"6": [27,30,46], "98": [11,10,14], ...}` — string index keys, `[R,G,B]`
  arrays. `LoadOverrides()` falls back to an embedded resource
  `Resources/palette_override.json` if the file doesn't exist — **that
  resource does not currently exist**, so overrides are empty by default
  today. A future session could ship the confirmed-good indices below as
  that embedded default, so users get the fix out of the box while still
  being able to add their own on top (note: current `LoadOverrides()` logic
  is file-OR-embedded, not merged — if you ship embedded defaults, decide
  whether user edits should merge with or fully replace them, and adjust the
  fallback logic accordingly, since right now the presence of a user file
  fully shadows the embedded one).

**Missing and needed for the UI to actually work: `Storage.SaveOverrides()`
does not exist yet.** Only `LoadOverrides()` is implemented. A palette
selector needs to add a save path (mirror the JSON shape above) and call
`RebuildLut()` after any change (it's cheap — just rebuilds the 256-entry
LUT, no daemon round-trip needed since this is purely client-side).

## Confirmed-correct override values to seed the UI with (Round 5, hardware-verified)

Derived from the user's own paired before/after screenshots of the real
Windows client (`Nautilus Research/Assets/BadPalette.png` /
`GoodPalette.png`) vs. the manual's intended rendering, self-validated in
software (MAE-against-ground-truth check) before ever touching hardware,
then live-confirmed on the daemon's wire palette:

| index | correct RGB | symptom this fixes |
|---|---|---|
| 0 | (3,3,3) | trivial, imperceptible |
| 6 | (27,30,46) | "dark grey renders as aqua/cyan blue" |
| 98 | (11,10,14) | "dark dark grey renders as brown" |
| 227 | (11,10,14) | same true color as idx 98 (not a coincidence — see below) |
| 233 | (59,62,72) | background-fill grey was wrong |
| 244 | (227,105,154) | "pink renders as orange" |

These are ALREADY deployed daemon-side (`KronosScreenRemoteDaemon/source/
palette_data.h`'s `nautilus_palette_fixes[]`, applied in `screenremote.c`'s
`fb1_open()` only when `fvar.bits_per_pixel==16`/Nautilus). A future
client-side override UI seeded with these same 6 values would be
*redundant* with the daemon fix, not wrong — harmless if both are active
(same values), and gives users on an un-updated daemon the fix too. Don't
be confused if you check the wire palette and these already look right —
that's the daemon fix, not a client bug.

**idx 98 and idx 227 resolving to the same true color is a hard clue**: this
is not a simple index permutation/remap you can solve with a formula — it's
a genuine per-index correction table, no shortcut.

## What's still wrong and unconfirmed — real gaps for the UI/future work

- Only 6 of 256 indices are confirmed. The other 250 are UNVERIFIED — could
  be right, could be wrong. Don't assume the rest of the table is fine just
  because these 6 are fixed.
- **"Lighter grey renders as virtually white"** — reported by the user,
  never pinned to a specific index. No large enough flat sample existed in
  the one screenshot pair analyzed. This is probably the single most
  valuable next thing to nail down via the same before/after-screenshot
  method (see below) — a UI element with a big light-grey area, or the
  eyedropper tool itself could let the user click the wrong-looking spot
  live and read off which index it is.
- The piano-photo thumbnail region on PROGRAM screens renders as full
  RGB noise/static under the current table — it's genuinely dithered across
  many indices, not fixable by a handful of point corrections. Likely needs
  either many more index corrections or is structurally unfixable via a
  256-entry palette remap alone (see "what this can't fix" below).

## How to find more correct values safely (the ONLY proven-safe method)

1. Get a paired screenshot: the Windows client's current (wrong) rendering
   of some screen, AND a reference image of what that same screen should
   look like (Korg manual, or a real Kronos/Nautilus that renders it right).
   They must be the exact same screen/state — same patch, same values, same
   layout — or the alignment step below is meaningless (this bit the
   investigation once this session — a screenshot pair from *different*
   patches looked alignable but wasn't, and produced a backwards result that
   had to be caught and reverted).
2. Resize one to match the other's pixel dimensions (nearest-neighbor, not
   smooth resampling — smooth resampling invents blended colors that don't
   correspond to any real palette index).
3. Invert the "wrong" image through the CURRENT palette table (nearest-color
   match per pixel) to recover the true index at every pixel.
4. For each index, take the majority-vote color from the "correct" image's
   co-located pixels — but ONLY over pixels whose neighborhood (e.g. 9x9) is
   a single recovered index, to throw out text-edge/photo-dither/alignment
   noise as noise instead of signal. This step is what took Round 5 from
   noisy ~10-25% agreement to clean 48-94% agreement on the same data.
5. Before shipping any correction, self-validate: repaint the recovered
   index frame through the candidate corrected palette and compare
   (mean absolute error) against the reference image. This is what caught
   that a full different-table swap (see below) was actually WORSE than
   doing nothing, despite looking individually plausible.

## Hard lessons from today — don't repeat these

- **A full palette table swap (any static table found via disassembly, even
  one that matches a specific symptom suspiciously well) made things
  measurably WORSE, not better**, when tested against real ground truth
  (MAE 93.6 vs. 61.6 for doing nothing). A compelling-looking match at 1-2
  indices is not evidence the whole table is right. Always validate the
  *whole* table's effect against ground truth before trusting it, never
  just the indices that first looked promising.
- **The real per-boot palette is never retained anywhere in host-readable
  memory** — not in Eva's process, not in the kernel modules, not on disk.
  `OmapNKS4UpdateColorPal(index,R,G,B)` (the function that actually carries
  real color data toward the physical panel) is called once at boot, its
  values pushed through a transient ring buffer and transmitted immediately
  — confirmed via both passive inspection and an active hardware hook that
  captured zero calls during live navigation. There is no static table left
  to go find, on Eva's side or the kernel-module side. Don't re-attempt a
  from-scratch "find the master table" search — it doesn't exist as a
  persistent artifact.
- **Do not attempt to catch this at boot via kernel module / boot-loader
  patching again on this specific unit.** This was tried this session
  (patching `/sbin/loadoa` + `/sbin/loadmod.ko`'s MD5 self-check) and, after
  fixing two real bugs in the approach (needed `-static` linking to avoid a
  glibc mismatch; one module parameter this unit's `OmapNKS4Module.ko`
  build rejects), triggered a real ext2 filesystem corruption event on
  reboot (likely surfacing pre-existing fragility from this unit's earlier
  undiagnosed self-shutdown history, worsened by repeated forced reboots).
  Recovered cleanly via `e2fsck -y -f` + restoring both files from backup,
  no data loss, but it's a real risk this unit specifically doesn't tolerate
  well, for a payoff (one boot's transient values) that wouldn't even give a
  stable reference table anyway. If this is ever revisited, do it only after
  a full raw disk image backup via the rescue USB, not casually.
- **`omapfb_setcolreg`** (the standard Linux fbdev color-register callback)
  is confirmed INERT on this hardware — gated on a `fix_info->visual==2`
  (`FB_VISUAL_TRUECOLOR`) check that's false here; `pseudo_palette` reads
  back all zeros. Don't waste time hooking it again.

## What this client-side approach fundamentally can't fix

A per-index RGB override only changes what the *remote view* renders for a
given index byte — it can't fix:
- The photo-thumbnail dithered-noise region (would need per-pixel/per-
  dither-pattern correction, not a 256-entry table).
- Anything that's wrong because the daemon is mis-decoding the raw fb1 bytes
  in the first place, rather than mis-coloring a correctly-decoded index
  (not currently believed to be the case — the "index-in-low-byte" decode is
  hardware-verified structurally correct — but worth keeping in mind if a
  "fix" doesn't hold up under the MAE-validation step above).
- The actual physical Nautilus LCD's own colors, which this whole mechanism
  never touches.

## Reference

Full research trail: `kronosology/docs/hardware/nautilus_color_palette.md`
(Rounds 1-7 as of this writing).
