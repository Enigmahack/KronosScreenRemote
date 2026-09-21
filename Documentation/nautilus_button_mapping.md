# Nautilus front-panel button label mapping

Source: provided directly by the user from hands-on knowledge of the physical
Nautilus unit (2026-09-19), NOT yet verified against the daemon/hardware.
**Unverified — do not implement a UI relabel or button re-dispatch from this
table without testing each row first.** Per `Nautilus Research/CLAUDE.md`, a
real Nautilus unit is in hand but currently has SysEx-over-USB access only —
no FTP/SSH/root/network access yet — so `KronosScreenRemoteDaemon`'s `BUTTON`
injection path (this table's whole reason for existing) is not testable on
real hardware until that arrives.

## What this describes

The daemon's `BUTTON <name>` control-port command (see
`KronosScreenRemoteDaemon/docs/api.md` §9, and `Core/Mode.cs` /
`Networking/DaemonCommand.cs`'s `PanelButton` enum in this repo) addresses a
button by its fixed NKS4 hardware scan code — the same code regardless of
what's silkscreened on the case. The working assumption (also unverified) is
that Nautilus reuses the same scan-code layout in different physical
positions/labels, i.e. sending `BUTTON COMBI` still does *something* on a
Nautilus, but the button the user would press by hand for that same scan
code is silkscreened "MODE", not "COMBI". This table is Kronos-code-name →
Nautilus-silkscreen-label, meant to drive a future Nautilus-specific
front-panel skin (see `Views/MainWindow.xaml.cs`'s `ApplyDeviceFamilyUi()` —
the scaffolding seam this would plug into once it exists).

**Caveat on the 6 QA (quick-access) buttons (A-F):** these are described as
user-programmable slots on the real hardware, not fixed functions. The
Setlist/Sequence/Sampling/Global/Disk/Exit → QA E/A/B/C/D/F mapping below is
today's factory-default assignment as observed by the user, not a hardware
constant — a user who reassigns their own QA slots would invalidate it. Any
future implementation should treat this row group as a default, not a fixed
truth, and ideally make it a user-editable setting rather than hardcoded.

## Mapping

| Code identifier | Wire token (`BUTTON <token>`) | Kronos label | Nautilus label | Confidence |
|---|---|---|---|---|
| `Mode.Combi` | `COMBI` | COMBI | MODE | user-reported |
| `Mode.Setlist` | `SETLIST` | SETLIST | QA **E** | user-reported, QA slot is reassignable |
| `Mode.Program` | `PROGRAM` | PROGRAM | PAGE | user-reported |
| `Mode.Sequence` | `SEQUENCE` | SEQ | QA **A** | user-reported, QA slot is reassignable |
| `Mode.Sampling` | `SAMPLING` | SAMPLING | QA **B** | user-reported, QA slot is reassignable |
| `Mode.Global` | `GLOBAL` | GLOBAL | QA **C** | user-reported, QA slot is reassignable |
| `Mode.Disk` | `DISK` | DISK | QA **D** | user-reported, QA slot is reassignable |
| `PanelButton.Help` | `HELP` | HELP | Data Increment | user-reported |
| `PanelButton.Compare` | `COMPARE` | COMPARE | Data Decrement | user-reported |
| `PanelButton.Exit` | `EXIT` | EXIT | QA **F** | user-reported, QA slot is reassignable |
| `PanelButton.Enter` | `ENTER` | ENTER | OCTAVE − | user-reported |
| NUM2 | `NUM2` | 2 | DYNAMICS | user-reported |
| NUM3 | `NUM3` | 3 | CONTROL SELECT | user-reported |
| NUM4 | `NUM4` | 4 | LATCH | user-reported |
| NUM5 | `NUM5` | 5 | ARP | user-reported |
| NUM6 | `NUM6` | 6 | DRUM | user-reported |
| NUM7 | `NUM7` | 7 | AUDIO IN | user-reported |
| NUM9 | `NUM9` | 9 | TAP TEMPO | user-reported |
| `PanelButton.NumDash` | `NUM_DASH` | − | MFX | user-reported |
| `PanelButton.NumDot` | `NUM_DOT` | . | TFX | user-reported |
| Data wheel | `WHEEL CW`/`WHEEL CCW` | (wheel) | unchanged | user-reported |

## Not yet mapped — needs testing

Explicitly flagged by the user as still needing hardware verification via
button-send testing:

- NUM0, NUM1, NUM8
- `PanelButton.Inc` / `PanelButton.Dec` (`INC`/`DEC`) — a separate button pair
  from Help/Compare on Kronos; unclear if/how they exist on Nautilus
- `PanelButton.TapTempo` (`TAP_TEMPO`) — note NUM9 is reported as the
  Nautilus *Tap Tempo* function, which may mean the dedicated `TAP_TEMPO`
  scan code has no physical equivalent on Nautilus, or maps somewhere else
- Sequencer transport: `SeqLocate`/`SeqRewind`/`SeqForward`/`SeqPause`/
  `SeqRecord`/`SeqStart`
- Sliders (`SLIDER`), knobs (`KNOB`), joystick/vector/ribbon, `VSLIDER`
- Touch coordinates — Nautilus's screen is 800×480 vs Kronos's 800×600
  (separate from this button-label question, but relevant to the same
  eventual Nautilus-skin work; the streaming/frame-size path already handles
  this via the handshake's reported width/height, see prior session notes)
