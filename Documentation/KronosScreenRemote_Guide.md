# Keyboard Screen Remote - User Guide

Windows client for the Keyboard Screen Remote system. View and interact with the touchscreen and front panel of a **Kronos** or **Nautilus** from your PC over a wired LAN connection.

Where a feature exists on only one of the two instruments, this guide says so. Everything else works the same on both.

---

## Requirements

- Windows 10 or Windows 11
- .NET 10 desktop runtime
- A Kronos or Nautilus running the Screen Remote daemon, installed and active
- Network connection to the instrument (USB network adapter or direct Ethernet; default IP `192.168.1.2`)

---

## Getting Started

### 1. Enter the instrument's address

Open **File → Settings... → Connection** and enter the **Instrument IP address** (e.g., `192.168.1.2`), or pick a named entry from **Saved connections** at the top of the tab. The daemon ports are fixed (stream 7373, control 7374).

### 2. Connect

Use **Connection → Connect**, or simply launch the app - it connects automatically using the saved address. Previously used addresses are listed under **Connection → Recent Connections**.

### 3. Log In

If no credentials are saved - or the saved credentials have stopped working - a login dialog appears. Enter the FTP username and password for the instrument. These are the same credentials used by the instrument's FTP service (vsftpd), set via `/etc/shadow`, `/korg/rw/screenremote/KronosNet.conf`, or the vsftpd password database.

Check **Save password** to skip the dialog on future connections. The same credentials are used for both the screen stream and the File Manager.

### 4. Connecting

The connection goes through a handshake:
- "TCP connected - sending handshake..."
- "Handshake OK - 800×600"

If credentials are wrong, an "Authentication Failed" message appears. If the connection times out after 10 seconds, a message lists likely causes: firewall blocking port 7373, daemon not running, or cable unplugged.

The app does not reconnect on its own after a network interruption; use **Connection → Connect** again.

### 5. Once Connected

The instrument screen is displayed live in the main window. The status bar shows the current mode, read from the daemon (see [Mode Detection](#mode-detection)). The app identifies whether it is talking to a Kronos or a Nautilus and adjusts its panels and menus to match.

---

## Interacting with the Instrument

### Touch Input

Click anywhere on the displayed screen to send a tap to the instrument at the corresponding position. The coordinates are scaled from the window display size back to the native 800×600 framebuffer space automatically.

- **Click** - tap (press + release)
- **Click and drag** - pen-down, move, pen-up (swipe or drag gestures). The drag must exceed 8 screen pixels before the touch-down is sent.

If touch calibration has been set up (see [Calibration](#calibration)), coordinates are corrected before being sent.

### Value Slider (Left Panel, Kronos only)

The left panel mirrors the Kronos front-panel **VALUE** slider and increment/decrement buttons. It is not shown on a Nautilus.

| Control | Action |
|---|---|
| INC button | Send a single increment step |
| DEC button | Send a single decrement step |
| Slider thumb | Drag up or down to send a continuous value (0–127). Top = 127, bottom = 0 |

The value slider command is sent only when the value changes. The left panel is visible in the **Full** layout while the value input is shown; it hides in the **Focused** layout or via **View → Hide Value Input**.

### Control Surface (Right Panel)

The right panel mirrors the physical front panel. Clicking any button sends the corresponding hardware button press to the instrument.

| Group | Buttons |
|---|---|
| Mode | Setlist, Combi, Program, Sequence, Sampling, Global, Disk |
| Navigation | Exit, Enter |
| Utility | Help, Compare |
| Number pad | 0–9, Dot, Dash |
| Data wheel | Drag up/down to scroll; mouse scroll wheel also works everywhere |

The lit mode button shows the current operating mode.

**On a Nautilus** the panel switches to the Nautilus layout, and the **Mode Select** menu lists the front panel's own Mode, Page and A–F buttons instead of the seven Kronos modes (F is the Write/Save quick-access slot). Bank Select and the status-bar mode menu are Kronos-only and are greyed out on a Nautilus.

### Bank Select (Kronos only)

Bank select is available from the **Bank Select** menu: Internal and User list the single letters A through G, and User (AA–GG) lists the doubled-letter pairs, which send a chord of the U and I buttons together.

### Keyboard Input

Click inside the screen panel to activate keyboard capture. While active, most host keystrokes are forwarded to the instrument as if typed on a connected USB keyboard. Most keys translate directly; uppercase and lowercase are handled via the character mapping.

**Physical key routing:** Numpad keys (0–9, minus, decimal) are routed to the corresponding number-pad buttons rather than typed as characters. This cannot be disabled, but these keys can be freed from physical routing by removing them from the input map (see [Input Mapping](#input-mapping)).

**Data wheel:** Scroll the mouse wheel to send data wheel ticks (CW/CCW). Ctrl+scroll zooms instead.

Click outside the screen panel - on the control surface, the wheel, or the menu bar - to release keyboard capture. The ⌨ indicator in the status bar shows the capture state (blue = active, gray slash = inactive, red slash = remote typing disabled via **Tools → Disable Remote Typing**).

### Mode Keyboard Shortcuts

By default, function keys F2–F8 switch the mode:

| Key | Kronos mode | Nautilus button |
|---|---|---|
| F2 | Setlist | E |
| F3 | Combi | Mode |
| F4 | Program | Page |
| F5 | Sequence | A |
| F6 | Sampling | B |
| F7 | Global | C |
| F8 | Disk | D |

These can be rebound in Settings → Key Bindings.

### Bank Select Shortcuts (Kronos only)

Bank shortcuts are unassigned by default. Assign them in Settings → Key Bindings under the "Bank" group.

### Sequencer Transport, Save and Tap Tempo

The status bar footer has a small transport row (Locate, Rewind, Fast-Forward, Pause, Record, Start/Stop) plus a separate **Save** button and a **Tap Tempo** button. The transport and Save buttons are greyed out - not hidden - when the current mode doesn't support them.

| Control | Behavior |
|---|---|
| Locate / Rewind / Fast-Forward / Pause | Momentary. Active only in Sequence mode |
| Record | Toggle; stays depressed while armed. Active only in Sequence mode. Stopping playback or changing mode clears it |
| Start / Stop | Toggle; the icon swaps between ▶ and ■. Active only in Sequence mode |
| Save | One-shot Write key press. Active in Setlist, Combi, Program, and Global modes |
| Tap Tempo | One tap per click; the instrument averages the taps itself, so two taps is enough. Available whenever connected, except in Sequence mode on a Nautilus (the first tap would snap the tempo to 99) |

REC/WRITE is one physical key on the instrument; the app splits its two roles onto two buttons so both stay visible regardless of mode.

---

## Display

### Zoom

- **Ctrl + mouse wheel** - zoom in/out.
- **`+` / `−`** - zoom in/out in 0.5× steps (range 2.5×–10×); `+` enables the zoom window automatically if it is off.
- **Toggle Zoom Window** (default: `Z`, or **View → Zoom Window**) - a floating magnification window that follows the cursor over the screen. The default level and window size are set in Settings → View.

### Aspect Ratio

The frame is always letterboxed to the native 800×600 (4:3) ratio - it is never stretched to fill the window.

### Fullscreen

Press `F` (default) to toggle fullscreen. The title bar is hidden; press `~` to show or hide the menu bar. `F` or `Esc` exits fullscreen.

### Hide Data Input / Hide Value Input

**View → Hide Data Input** and **View → Hide Value Input** collapse the right-hand and left-hand panels. Both can be set to start hidden (Settings → General). The value input panel does not exist on a Nautilus.

### Layout Presets

Two layout presets are available (View → Layout Preset):

| Preset | Description |
|---|---|
| **Full** | Value slider, screen panel, and control surface side by side (default) |
| **Focused** | Screen fills the window; a narrow rail on the right edge can be clicked to temporarily expand the control surface. The value slider is hidden |

### Window Size

**View → Window Size** (or `Ctrl+1`–`Ctrl+5`) scales the whole window to 75%, 100%, 125%, 150% or 200%. **View → Always on Top** keeps the window in front of other applications.

### Image Adjustments and Scaling

**View → Scaling Quality** chooses how the 800×600 screen is resampled when enlarged: Sharp (crisp pixels), Smooth (bilinear), or High Quality (default). **View → Image Adjustments...** (also Settings → Image) offers brightness, contrast, gamma, saturation and sharpen sliders with a live preview; Cancel undoes the preview.

---

## VGA Mirror (Kronos only)

When enabled, the daemon also copies the Kronos screen to the VGA output (`/dev/fb0`). This lets you connect an external monitor directly to the Kronos VGA port. A Nautilus does not support it.

Toggle with the `M` key (default) or the command palette, or enable it by default in Settings → General → Enable VGA mirror on connect.

### Screensaver

When the VGA mirror is active and the screen has been static for the screensaver timeout period, the VGA output is blanked automatically. It wakes on any screen change. The timeout is set in Settings → General (0 to disable).

---

## Screenshots

| Action | Description |
|---|---|
| **Save Screenshot...** (`Ctrl+S`) | Shows a save dialog to choose filename and location |
| **Quick Save Screenshot** | Saves instantly to the Screenshot Directory (or desktop if unset) |
| **Copy Frame to Clipboard** | Copies the current frame to the system clipboard |
| **Open Screenshots Folder** | Opens the screenshot output directory in Explorer |

These are in the File menu and the screen's right-click menu. The output folder is set in Settings → General → Screenshots.

---

## File Manager

The File Manager (Tools → File Manager...) is a dual-pane file browser for transferring files between your PC and the instrument over FTP.

| Pane | Contents |
|---|---|
| Left | Your local PC (starts at the Desktop folder, or a folder you set) |
| Right | Instrument filesystem (`/` by default) |

It uses the same FTP credentials as the screen stream. The instrument's FTP service (vsftpd) must be running.

### Transferring Files

- **Upload**: drag files from the left pane to the right, or select and click the upload toolbar button.
- **Download**: drag files from the right pane to the left, or select and click the download toolbar button.
- When a conflict occurs (file already exists), a dialog offers **Rename**, **Overwrite**, **Skip**, or **Cancel**, with an option to apply the choice to all remaining conflicts.

### Navigation and File Operations

| Key / Action | Effect |
|---|---|
| Double-click a folder | Navigate into it |
| `..` row / Backspace / ↑ button | Go up to the parent folder |
| Hover over a folder while dragging | Navigate into it (dwell-to-navigate) |
| F5 | Refresh the active pane |
| F2 | Rename the selected item |
| Del | Delete the selected item (confirmation required) |
| Ctrl+A | Select all items in the active pane |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste within a pane |
| Toolbar - New Folder | Create a folder in either pane |
| Column header click | Sort by Name, Size, or Modified date |
| Drive selector (local pane) | Switch between drives on your PC |

Right-click a folder in the local pane for **Set Default Start Folder**. Rubber-band selection (click-drag on empty space) and drag-scroll (drag to the top or bottom edge of a pane) are supported.

---

## Calibration

The calibration system corrects for systematic touch-position offsets between what is displayed in the client window and where the instrument registers the touch.

Press `C` (default), or use **Tools → Calibration**, to enter calibration mode. It has two stages:

**Observe mode**
- Click sends a tap with the current calibration applied.
- Right-click adds an indicator dot at that position, or removes the nearest existing dot.
- `W` enters Warp mode; `C` exits calibration.

**Warp mode**
- Drag the blue nodes to shift the correction mesh.
- Right-click removes the nearest bias dot.
- `S` saves the mesh, `R` resets it to identity, `X` clears all bias dots, `W` returns to Observe mode.

Calibration applies a bilinear mesh warp: client coordinates are mapped through the mesh before being sent as touch events. If all nodes are at their natural positions, no correction is applied. The grid size (3×3, 4×4, 5×5) is set in **Tools → Calibration Grid Size**; changing it clears existing calibration data. `Ctrl+Z` / `Ctrl+Y` undo and redo in calibration mode.

**Reset calibration:** Drag all nodes back to their natural positions, or use Settings → General → Reset All Settings (this resets everything, not just calibration).

---

## Test Mode (Kronos only)

Access via **File → Settings... → Debug → Enter Instrument Test Mode...**. This sends the Kronos into its built-in hardware test mode for diagnostics and hardware verification. The button is hidden when connected to a Nautilus, which has no equivalent.

> **Warning:** All unsaved changes on the instrument will be lost, and the instrument must be restarted after testing is complete. A confirmation dialog warns before proceeding. Only use this if you understand the risk.

---

## Macros

Macros record a sequence of key presses and replay them on demand. They are assigned a keyboard trigger that fires them globally while the app is in focus.

### Creating a Macro

1. Open Settings → Macros.
2. Click **Add**.
3. Enter a description.
4. Click **Trigger** and press the key combination you want to trigger the macro (must include at least one modifier: Ctrl, Alt, or Shift).
5. Click **● Record** and press the keys for your macro sequence.
6. Click **■ Stop** when done.
7. Adjust **Step delay** if needed (default 50 ms between steps).
8. Click **▶ Play** to test.
9. Click OK.

### Macro Triggers

Triggers require a modifier key. Single keys without a modifier are not accepted as triggers (to avoid conflicting with normal keyboard use).

### Step Delay

The delay between macro steps is configurable per-macro (10–500 ms). A higher delay gives the instrument more time to process each step.

---

## Input Mapping

The input map lets you bind a host key (optionally with Shift) to a specific Linux keycode sent to the instrument, bypassing the normal character-map translation.

This is useful for:
- Triggering functions that do not have a named button or standard key mapping.
- Remapping keys that the instrument interprets differently from what the standard map produces.
- Sending key combos that do not exist on the host keyboard layout.

Open Settings → Input Mapping.

| Column | Meaning |
|---|---|
| Host Key | The Windows key to intercept (with optional Shift) |
| → Sends | The Linux keycode (1–767) sent to the instrument |
| Label | Optional description |

**Adding a mapping:**
1. Click **Add** (or select an existing entry to edit).
2. Click **Host key** and press the key to capture.
3. Enter the raw Linux keycode.
4. Check **Send Shift** if needed.
5. Click **Save**.

**Keys that cannot be mapped:** Numpad 0–9, numpad minus, and numpad decimal are reserved for physical button routing and cannot be remapped. The UI will reject them.

---

## Settings

Open via **File → Settings...**. Export, import and reset are described at the end of this section.

### General

| Setting | Description |
|---|---|
| Prompt before quitting | Show a confirmation dialog before closing the app |
| Hide data / value input panel on startup | Start with the corresponding panel hidden |
| Reverse mouse scrolling direction | Flip the data wheel direction |
| Screenshots → Output folder | Folder where screenshots are saved (empty = desktop) |
| Enable VGA mirror on connect | Kronos only. Automatically enable the VGA mirror when connected |
| Screensaver timeout | Seconds of inactivity before blanking the VGA output (0 = disabled) |
| Reset All Settings | See [Import / Export / Reset](#import--export--reset) |

### Connection

| Setting | Description |
|---|---|
| Saved connections | Named instruments. **+** saves a new entry (pre-filled from the fields below), **-** removes the selected one, **Edit...** changes its name, IP address, FTP login and port. Choosing an entry fills in the IP address and FTP login below |
| Instrument IP address | IP address or hostname of the instrument |
| FTP Username / Password | FTP credentials - used for both the screen stream and the File Manager. Also set via the login dialog on first connect |
| FTP Port | TCP port for FTP file access used by the File Manager (default 21) |
| Clear FTP Credentials | Forget the saved username and password |

### Streaming

| Setting | Description |
|---|---|
| Stream mode | **Change-driven** (the instrument pushes a frame only when the screen changes) or **Pull** (the app requests each frame individually) |
| Max frame rate | Maximum frame rate (1–15). Change mode caps the delivery rate; Pull mode sets the polling interval |

**Change mode** (default) uses less bandwidth and CPU and is recommended for most use.

**Pull mode** requests frames at the fixed rate regardless of whether the screen changed. It may help if frames are occasionally missed. Streaming changes take effect on the next connect.

### View

| Setting | Description |
|---|---|
| Default window size | Last Used / Small / Medium / Large / Maximized |
| Default zoom level | Magnification level for the zoom window (2.5×–5.0× here; the zoom window itself goes to 10×) |
| Zoom tool size | Size multiplier for the floating zoom window |

### Image

Upscaling filter (Sharp / Smooth / High Quality) and the brightness, contrast, gamma, saturation and sharpen adjustments, with **Reset Adjustments**.

### Key Bindings

All rebindable actions are listed. Double-click a row to capture a new key combination (hold Ctrl/Alt/Shift first to set a chord). Press Escape to cancel; select a row and press Delete to clear its binding.

**Rebindable actions include:**

- Quit, Fullscreen, Zoom Window, Zoom In/Out, Help, Calibrate, Mirror (Kronos only)
- Hide Data Input / Hide Value Input
- Mode: Setlist / Combi / Program / Sequence / Sampling / Global / Disk
- Sequencer: Locate, Rewind, Forward, Pause, Record, Start/Stop, Save, and Tap Tempo
- Bank select: I-A through I-G, U-A through U-G, U-AA through U-GG (Kronos only)

### Input Mapping and Macros

See [Input Mapping](#input-mapping) and [Macros](#macros).

### Debug

| Setting | Description |
|---|---|
| Enable debug logging | Write verbose connection and frame diagnostics to the log file. Takes effect immediately; can grow the log quickly |
| Button Injector... | Shown while debug logging is on. Sends any daemon-recognized front-panel button by name - used for mapping the Nautilus's relabeled buttons |
| Enter Instrument Test Mode... | Kronos only. See [Test Mode](#test-mode-kronos-only) |

### MIDI/SysEx

| Setting | Description |
|---|---|
| MIDI transport | **Auto** (prefer USB) / **TCP** (daemon) / **USB** (direct) |
| Monitor MIDI | Connect/read the live MIDI stream (needed for follow, the monitor, and dumps) |
| SysEx Poll on Changes | Check in via SysEx when a Program Change or Bank Select is received. Requires Monitor MIDI |
| Pull Names on Program Change | Fill the performance name as you navigate, without a full names pull |
| Proactive SysEx Polling | Periodically query the current performance on a schedule, regardless of MIDI activity. Can slow the instrument during the check-in |
| Poll interval | Seconds between proactive polls |
| Value slider CC# | MIDI controller number the instrument's VALUE slider transmits (default 18) |

### Librarian

| Setting | Description |
|---|---|
| Merge behavior | **Temporary Memory** (staging cleared on close) or **Local Storage** (persisted across sessions) |
| Programs / Combis: preserve duplication | See [Merge Window](#merge-window-right) |
| Full sync on launch | Off by default. Pulls every bank as soon as the Librarian opens, rather than only the banks whose digest changed. Pull only - it never pushes, so nothing reaches the instrument without an explicit Sync |
| Force destructive write | Off by default. Treats the keyboard library as the source of truth: 2-Way Sync overwrites banks that changed on the instrument since the last pull, without asking. Front-panel edits made since then are lost, and the pre-write backup does not cover them. A red banner in the Librarian shows while it is armed |

### Sample Editor

Playback output device and the Create Zone preferences (position, key range, and original-key position).

### Import / Export / Reset

- **File → Export Settings...** - save all settings (including key bindings, macros, and the input map) to a JSON file.
- **File → Import Settings...** - load settings from a previously exported JSON file.
- **Reset All Settings** (Settings → General) - permanently deletes all saved settings, key mappings, calibration data, and palette overrides. The app returns to its out-of-the-box state.

---

## MIDI / SysEx

The app monitors the instrument's live MIDI output - program-change and mode-change follow, the VALUE-slider mirror, and the SysEx/MIDI traffic window all run off it.

### Transport selection

The MIDI link is chosen in **Settings → MIDI/SysEx** (default **Auto**):

- **USB** - the instrument directly connected via USB-MIDI (no daemon/network needed). Fast and exclusive - if a DAW already holds the port, the app falls back to TCP.
- **TCP** - the daemon's MIDI bridge (port 9875), used when the screen is connected.

The footer badge shows which link is live: **USB** (green, fast), **DIN** (amber, a 5-pin interface bridging the instrument), **TCP** (blue, network), or **-** (none). Hot-plug is automatic - plugging in a USB instrument switches over live.

### MIDI Monitor (Tools → MIDI Monitor...)

- Live traffic log with per-message decode (notes, CC, PC, SysEx function codes), per-type filter buttons (click cycles On → Solo → Off), and Copy / Copy All Shown.
- A virtual piano and pitch joystick for sending notes and pitch bend on the selected OUT CH. Right-click a piano key or the joystick to assign a physical keyboard key to it.
- **Pull names** (right-click the current-performance display in the status bar → *Pull names from Instrument...* → This Program / This Bank / This Mode / All) - dumps program/combi names once and caches them, so program-change display is flash-free (a bank is re-dumped only after its storage digest changes).

> **Note:** SysEx receive ("Enable Exclusive") must be on in the instrument's Global/MIDI settings for the monitor, name sync, and set-list features to work. If a DAW is running, it may hold the USB port - the app auto-falls-back to TCP.

---

## Librarian (Tools → Librarian...)

The Librarian is a full library manager for programs, combis, set lists, drum kits, and wave sequences: it can pull the whole instrument into a keyboard library, import `.pcg` files, stage objects in a Merge Window, and place them back onto the instrument with dependency resolution. Everything is undoable (Ctrl+Z) and every write to the instrument is preceded by a backup.

### PCG pane (left)

Load a `.pcg` file to browse its banks. Pull objects into the Merge Window (right-click or drag):

- Pulling a **set list** transitively pulls the combis it references, which in turn pull their programs.
- Pulling a **combi** pulls its programs.
- References that don't resolve inside the loaded file are reported as gaps - they stay unresolved until a file that does contain them is loaded and pulled.
- The search box above the tree matches name, bank (e.g. `I-A`), category, EXi engine type (e.g. `AL-1` matches both a name containing it and a program that *is* one), and what the object itself references. Case-insensitive.
- Loading a `.pcg` also resolves EXs/3rd-party **sample bank names** from the shipped EXs catalog (a local read - no connection, no login). A catalog hit identifies the product; it is not proof the pack is installed on the instrument.

### Keyboard Library (center)

- The **Sync** button pulls from and/or pushes to the instrument, depending on the mode chosen in its ▾ dropdown. The library is stored on disk (content-addressed, with per-bank SHA-1 digests).

  | Mode | Behavior |
  |---|---|
  | **Pull Only** | Replaces the keyboard library with what is on the instrument. Pending local changes are discarded (you are asked first) |
  | **Push Only** | Writes every pending local change to the instrument. If the instrument has changed since the last sync, you are asked before overwriting. Shown in red because it overwrites the instrument |
  | **2-Way Sync** | Pulls the library, then pushes every pending local change |

  **Force Full Sync** (below the button) pulls every object rather than only what changed; it applies to the pull half only.
- Cut/copy/paste to rearrange within the library, including whole-bank moves (Program banks can be copied across an EXi/HD-1 boundary, which stages a bank-type reformat for the next push).
- Conflicts (the bank changed on the instrument since baseline) are flagged, never silently overwritten; **Resolve Conflicts** pushes your copy over the instrument for every conflicted object.
- Read-only factory banks (GM, g1–g9, gd) are browseable but never writable.
- Delete and Clear Changes affect the keyboard library only - hardware is untouched until you sync. Clear History deletes the local audit log alone.

### Merge Window (right)

A staging area between a loaded `.pcg` file / the keyboard library and the instrument:

- Stage objects from the PCG pane or from Keyboard Library ("Move to Merge Window").
- **Auto-Fill** places everything staged into the next free slots of the correct type - dependencies are placed before their referrers, so a combi's timbres point at where its programs actually landed. Placement follows the Merge Window's own display order (source bank, then slot), so re-copying the same `.pcg` and auto-filling again lands in the same order every time.
- Auto-Fill sends nothing to the instrument - it only stages, exactly like dragging items across yourself. Review the result, then sync to push. Anything that doesn't fit (no bank of the matching type has room) stays staged.
- Placing is address-sensitive (you choose the destination); dependencies are resolved automatically.
- **Force Overwrite**: placing onto a slot another combi or set list still references normally refuses, to avoid silently breaking that reference. Force Overwrite places anyway - those referrers then resolve to the *new* object, and the old occupant is diverted to the session clipboard rather than lost.
- **Object Dependencies** (bottom right): red rows at the top are dependencies nothing staged provides. Right-click one to search a `.pcg` for it; anything found is staged, so the gap can be filled before you sync. Below them is every program/combi/drum kit/wave sequence the selection references, nested ones included - double-click a row for more info.
- Tree dots and tints: a dot marks an object staged or referenced more than once, a blue dot marks a sample reference (legend at the bottom of the window), and a conflicted, pending-delete, or read-only row is tinted instead of dotted.
- **Preserve duplicate Programs/Combis** (Merge Window toolbar, mirrored in Settings → Librarian): when checked, placing staged content that already exists in Keyboard Library still writes a fresh copy ("preserve duplication"); when unchecked, the existing byte-identical copy is reused instead of consuming a slot. Combis are compared *after* their program references are re-pointed at local reality, so a re-copied chain still matches. Defaults: Programs reused (unchecked), Combis copied as-is (checked).
- **Merge behavior** (Settings → Librarian): **Temporary Memory** clears the staging when the app closes; **Local Storage** persists it across sessions. Switching between them takes effect as soon as you press OK, even with the Librarian open, and carries whatever is already staged across: Temporary Memory → Local Storage writes the current staging out immediately, and Local Storage → Temporary Memory deletes the stored snapshot while keeping the staging in memory for the rest of the session. The snapshot is deleted rather than left on disk so that switching back later cannot silently re-adopt a batch from an old session.
- **Full sync on launch** (Settings → Librarian): off by default. Pulls every bank the moment the Librarian opens instead of only the changed ones. It runs unattended, so it is deliberately a *pull only* - it never pushes, and it waits for the local-library index and the SysEx probe before starting.
- **Force destructive write** (Settings → Librarian): off by default. Normally a bank whose contents moved on the instrument since the last pull is excluded from the push and flagged as a conflict. With this on the keyboard library wins outright and 2-Way Sync overwrites those banks silently - the standing equivalent of clicking **Resolve Conflicts** on every conflict. Anything edited at the front panel since the last pull is lost. Note that the pre-write backup does **not** protect those slots: it is built from this library's last known copy of each object, which in exactly these banks is the stale one - so the `.syx` written before the push cannot restore what was on the instrument. It does *not* disable the gates that catch writes the instrument would reject (a missing reference, an HD-1 body headed for an EXi bank, or a bank changing mid-write); those still refuse.

---

## Sample Editor (Tools → Sample Editor...)

View and edit `.KSC` / `.KMP` / `.KSF` sample content directly on disk - key ranges, loop points, sample rate, and destructive DSP edits. It works on a local copy; nothing reaches the instrument from here until you push it (the File menu has Pull and Push commands for collections, multisamples and samples; the File Manager and Librarian also move files).

- Open a `.KSC` collection via File → Open... (or drag-and-drop) to populate the tree on the left. Selecting a multisample shows its piano keymap and zone list; selecting a zone loads its sample into the waveform view.
- **Multisample panel**: the dropdown and Create/Rename/Delete buttons pick which multisample is being edited. Click a piano key to jump to the zone covering it, drag a zone's boundary to resize it, drag a zone to reorder it.
- **Zone panel**: Index / Sample / Orig. Key / Top Key fields for the selected zone. Create adds an empty zone at the end of the keymap; Import Sample... decodes one or more audio files into the collection and assigns the first to the selected zone.
- **Waveform controls** (Play/Stop `Space`, Pause, Locate Start/End `Home`/`End`, Rewind/Fast-Forward, Zoom In/Out `Ctrl+`/`Ctrl-`, Zoom to Selection, Fit `Ctrl+0`, Undo/Redo). The mouse wheel over the waveform zooms; uncheck **Scroll to Zoom** to scroll the pane instead.
- **LOCAL EDITS** (orange panel): Select/Move tool, Use Zero (snap to zero-crossings), Loop Lock, and destructive DSP buttons - Normalize, Amplify, Soften, Trim Silence, Reverse, Remove DC Offset, Insert Silence. These only touch this app's own in-memory buffer and undo stack.
- **KEYBOARD** (blue panel): fields written into the `.KSF` and reflected on the hardware once pushed - Loop Enabled and its Sample Start / Loop Start / Loop End / Loop Tune points, plus the Reverse and +12dB Boost flags.
- **Save Changes** (`Ctrl+S`) writes every pending edit to disk. **Edit → Revert** discards pending edits.
- A stereo pair (two multisamples with a shared name and opposite `-L` / `-R` suffix) shows both channels stacked in the waveform view, and an edit to one mirrors onto its partner.

---

## Mode Detection

The current operating mode is read from the daemon's **STATE** command (polled every 500 ms). The daemon reports it from the instrument's own live state, falling back to its own framebuffer pixel detection during early boot - so the mode shown is exact and doesn't depend on client-side image matching.

- The mode is shown in the status bar and drives the lit mode button on the control surface.
- Mode change buttons are ignored until the daemon confirms the board has finished booting (its `BOOT=` gate), so a stray press during boot can't light the wrong button.
- The daemon also reports a program-edit context (`EDITCTX`), which lights the Combi or Sequence button while you're editing a Program from inside one.

---

## Status Bar

The status bar at the bottom of the window shows:

| Element | Description |
|---|---|
| Coloured dot + text | Connection state: green = connected, amber = connecting, red = disconnected |
| ⌨ keyboard icon | Keyboard capture state - right-click to enable/disable keyboard send |
| FPS | Measured incoming frame rate while connected |
| Latency | Round-trip network latency to the instrument |
| Notification bubble | Click to open the log file; turns red on errors |
| Keyboard Info | Opens a pane displaying CPU, memory, temperature, and storage stats |
| VU meter | Audio level of a local Windows device (e.g. your DAW output). Click ▲ to pick the device; choice is saved |
| MIDI link badge | USB (green) / DIN (amber) / TCP (blue) - which link carries MIDI/SysEx (see [MIDI / SysEx](#midi--sysex)) |
| Change / Pull | Active streaming mode for the current connection |
| Tap Tempo, Save, transport | See [Sequencer Transport, Save and Tap Tempo](#sequencer-transport-save-and-tap-tempo) |
| Mode | Current operating mode. On a Kronos, right-click to change mode; on a Nautilus the right-click mode menu is disabled |

---

## Tray Icon

The main window can minimise to the system tray. Right-click the tray icon to show the window, reconnect, disconnect, or quit.

---

## Command Palette

`Ctrl+K` (or **Help → Command Palette**) opens a fuzzy-search launcher for all app commands. Start typing to filter; press Enter or click an entry to run it. Useful for infrequently used actions without navigating menus.

---

## Keyboard Shortcut Reference

| Key | Action |
|---|---|
| `F1` | Open help window |
| `F2`–`F8` | Switch mode (Kronos: Setlist through Disk; Nautilus: E, Mode, Page, A–D) |
| `C` | Toggle calibration mode |
| `F` | Toggle fullscreen |
| `M` | Toggle VGA mirror (Kronos only) |
| `Q` | Quit |
| `Z` | Toggle zoom window |
| `+` / `−` | Zoom in / zoom out (enables zoom automatically if off) |
| `Esc` | Send EXIT to the instrument / exit fullscreen / dismiss overlays |
| `Enter` | Send ENTER to the instrument |
| `Ctrl+1`–`Ctrl+5` | Window size: 75% / 100% / 125% / 150% / 200% |
| `Ctrl+K` | Open command palette |
| `Ctrl+S` | Save screenshot |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo (calibration mode) |
| `~` (fullscreen) | Show / hide the menu bar while in fullscreen |
| Mouse scroll | Data wheel (over frame or control surface) |

All shortcuts (except Ctrl combos) are rebindable in Settings → Key Bindings.

---

## Data Files

Application data is stored next to the executable (portable - the exe's own folder):

```
<exe directory>/
  settings.json            - connection, streaming, display, MIDI settings, key bindings, credentials
  raw_key_mappings.json    - input map entries
  macros.json              - recorded macro sequences
  cal_data.json            - calibration mesh
  palette_override.json    - palette overrides
  screenremote.log         - diagnostic log

  local_library/           - the Librarian's keyboard library (index.json, oplog.jsonl, content-addressable blobs)
  name_cache.json          - per-instrument program/combi name cache (program-change follow)
  setlist_cache.json       - per-instrument set-list cache
  dumped_banks.json        - per-instrument ledger of name banks already dumped
  category_names_cache.json - per-instrument category names (from a Global dump)
  librarian_backups/       - timestamped .syx backups taken before every instrument write
  local_library_clipboard.json - the Librarian's cross-session clipboard
```

All files are JSON (the keyboard library uses JSON + append-only op-log + SHA-1-addressed blobs) and can be hand-edited. **File → Export/Import Settings** backs up/restores `settings.json` (including key bindings and macros); the keyboard library and caches are separate.

> **Note:** the keyboard library and caches are keyed per instrument (by host for TCP, by device match for USB), so reconnecting to the same instrument reuses them.

---

## Troubleshooting

**Login dialog appears on every connect / "Authentication Failed":**
- Saved credentials are wrong or have changed. Use Settings → Connection → Clear FTP Credentials and try again.
- The instrument's FTP service (vsftpd) must be running - check with `ps | grep vsftpd` via SSH.
- Credentials can be tested from a command line: `ftp 192.168.1.2` (or your instrument's IP).
- If using KronosNet.conf, ensure the file at `/korg/rw/screenremote/KronosNet.conf` is readable and correctly formatted (`username:password`, one entry per line).
- **Note:** the daemon also accepts the username `kronos` with the device's PublicID as a password (an emergency recovery path for screen connect).

**Connection times out after 10 seconds:**
- Check that the instrument is powered on and the `screenremote` daemon is running (`ps | grep screenremote` via SSH).
- Verify the IP address and ports in Settings → Connection.
- Check Windows Firewall - it may block outbound connections to port 7373.
- Some antivirus/VPN drivers intercept socket I/O and delay connections. The 10-second watchdog is implemented by closing the socket, which bypasses this reliably.

**Screen appears but touch does nothing:**
- Confirm the control port (7374) is reachable - try `telnet 192.168.1.2 7374`.
- Check that the stream client connected first (access control: only the stream client IP can send control commands).
- If using a VPN or multiple network interfaces, the daemon may have bound to a different LAN IP. Check `dmesg | grep screenremote` on the instrument for the bound address.
- The daemon may still be in its boot gate (BOOT=1) - mutating commands return `ERR BOOTING` until it clears.

**Keys do not produce the expected characters:**
- The instrument's character mapping is inverted from standard keyboards: unshifted keys produce uppercase, shifted keys produce lowercase. This is intentional.
- Caps Lock is emulated by injecting Left Shift - if a letter is stuck lowercase, check the keyboard status (⌨ indicator) and that Shift was released.
- Check the input map (Settings → Input Mapping) for conflicting entries.
- Numpad keys route to physical buttons and cannot be used for text input.

**Frame is frozen / no updates:**
- Use **View → Refresh Display** to force a full frame resend.
- In Change mode, if the instrument screen is genuinely static, no frames are sent - this is normal.

**VGA mirror turns off after a while (Kronos):**
- The screensaver timeout has fired. Reduce or disable it in Settings → General → Screensaver timeout.

**Mode is shown as "Unknown" / mode buttons don't light:**
- The daemon's boot gate is still closed (BOOT=1) - the daemon refuses mutating commands and reports mode as 0 until the board has finished booting. Wait a few seconds after power-on; the app disables mode presses until it clears.
- The daemon's mode module couldn't resolve (check MODE_DETAIL on the daemon).
- You're not connected (mode follows the daemon's STATE poll, which needs a connection).
