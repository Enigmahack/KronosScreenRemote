# Kronos ScreenRemote - User Guide

Windows client for the Kronos ScreenRemote system. View and interact with the Kronos touchscreen from your PC over a wired LAN connection.

---

## Requirements

- Windows 10 or Windows 11
- .NET 10 desktop runtime
- Kronos running with the `Kronos Screen Remote` daemon installed and active
- Network connection to the Kronos (USB network adapter or direct Ethernet; default IP `192.168.1.2`)

---

## Getting Started

### 1. Connect to the Kronos

Open the application. The connection bar is at the top of the window. Enter the Kronos IP address (e.g., `192.168.1.2`) and press **Connect** (or the Enter key).

If the address was used before, the host drop-down shows recent connections.

The app sends a UDP discovery probe to detect the daemon's stream and control ports automatically. If discovery fails, the configured ports are used (default 7373 / 7374).

If the Kronos address was used before and credentials were saved, the app connects without prompting.

### 2. Log In

On first connect - or when saved credentials have expired - a login dialog appears. Enter the FTP username and password for the Kronos. These are the same credentials used by the Kronos FTP service (vsftpd), set via `/etc/shadow`, `/korg/rw/screenremote/KronosNet.conf`, or the vsftpd password database.

Check **Save password** to skip the dialog on future connections. The same credentials are used for both the screen stream and the File Manager.

### 3. Connecting

The connection goes through a handshake:
- "TCP connected - sending handshake..."
- "Handshake OK - 800×600"

If credentials are wrong, an "Authentication Failed" message appears. If the connection times out after 10 seconds, a message lists likely causes: firewall blocking port 7373, daemon not running, or cable unplugged.

### 4. Once Connected

The Kronos screen is displayed live in the main window. The status bar shows the current mode (Setlist, Combi, Program, etc.), read from the daemon (see [Mode Detection](#mode-detection)).

---

## Interacting with the Kronos

### Touch Input

Click anywhere on the displayed screen to send a tap to the Kronos at the corresponding position. The coordinates are scaled from the window display size back to the native 800×600 framebuffer space automatically.

- **Click** - tap (press + release)
- **Click and drag** - pen-down, move, pen-up (swipe or drag gestures)

If touch calibration has been set up (see [Calibration](#calibration)), coordinates are corrected before being sent.

### Value Slider (Left Panel)

The left panel mirrors the Kronos front-panel **VALUE** slider and increment/decrement buttons.

| Control | Action |
|---|---|
| INC button | Send a single increment step to the Kronos |
| DEC button | Send a single decrement step to the Kronos |
| Slider thumb | Drag up or down to send a continuous value (0–127). Top = 127, bottom = 0 |

The value slider command is sent only when the value changes. The left panel is visible in the **Full** layout when controls are shown. It hides automatically in **Focused** layout or when controls are hidden.

### Control Surface (Right Panel)

The right panel mirrors the physical Kronos front panel. Clicking any button sends the corresponding hardware button press to the Kronos.

| Group | Buttons |
|---|---|
| Mode | Setlist, Combi, Program, Sequence, Sampling, Global, Disk |
| Navigation | Exit, Enter |
| Utility | Help, Compare |
| Number pad | 0–9, Dot, Dash |
| Data wheel | Drag up/down to scroll; mouse scroll wheel also works everywhere |

Bank select is available from the **Bank Select** menu (I-A through I-G, U-A through U-G, and chord banks U-AA through U-GG).

### Keyboard Input

When the Kronos screen is focused (click on the frame), host keyboard events are forwarded to the Kronos. Most keys translate directly; uppercase and lowercase are handled via the Kronos character mapping.

**Physical key routing:** Numpad keys (0–9, minus, decimal) are routed to the corresponding Kronos number-pad buttons rather than typed as characters. This cannot be disabled, but these keys can be freed from physical routing by removing them from the raw key map (see [Raw Key Map](#raw-key-map)).

**Data wheel:** Scroll the mouse wheel over the frame to send data wheel ticks (CW/CCW).

### Mode Keyboard Shortcuts

By default, function keys F2–F8 switch the Kronos mode:

| Key | Mode |
|---|---|
| F2 | Setlist |
| F3 | Combi |
| F4 | Program |
| F5 | Sequence |
| F6 | Sampling |
| F7 | Global |
| F8 | Disk |

These can be rebound in Settings → Key Bindings.

### Bank Select Shortcuts

Bank shortcuts are unassigned by default. Assign them in Settings → Key Bindings under the "Bank" group.

---

## Display

### Zoom

- **Mouse wheel** over the frame - zoom in/out while keeping the area under the cursor centred.
- **Toggle Zoom Window** (default: `Z`) - opens a floating magnification window. The zoom level and window size are set in Settings → View.

### Aspect Ratio

The frame is always letterboxed to the Kronos' native 800×600 ratio - it is never stretched to fill the window. (There is no longer an aspect-lock toggle.)

### Fullscreen

Press `F` (default) to toggle fullscreen. The title bar and controls are hidden in fullscreen; move the mouse to the top edge to reveal them temporarily.

### Hide Controls

Press the collapse button on the button rail to hide the right-side control panel. Reassignable in Settings.

### Layout Presets

Two layout presets are available (View → Layout Preset):

| Preset | Description |
|---|---|
| **Full** | Value slider, screen panel, and control surface side by side (default) |
| **Focused** | Screen fills the window; a narrow rail on the right edge can be clicked to temporarily expand the control surface. The value slider is hidden |


---

## VGA Mirror

When enabled, the daemon also copies the Kronos screen to the VGA output (`/dev/fb0`). This lets you connect an external monitor directly to the Kronos VGA port.

Toggle via **View → VGA Mirror** or the `M` key (default), or enable it by default in Settings → VGA Output → Enable on connect.

### Screensaver

When the VGA mirror is active and the Kronos screen has been static for the screensaver timeout period, the VGA output is blanked automatically. It wakes on any screen change. The timeout is configurable (Settings → VGA Output → Screensaver timeout; 0 to disable). It can also be changed at runtime from the control port.

---

## Screenshots

| Action | Description |
|---|---|
| **Save Screenshot...** (`Ctrl+S`) | Shows a save dialog to choose filename and location |
| **Quick Save Screenshot** | Saves instantly to the Screenshot Directory (or desktop if unset) |
| **Copy Frame to Clipboard** | Copies the current frame to the system clipboard |
| **Open Screenshots Folder** | Opens the screenshot output directory in Explorer |

The output directory is set in Settings → General → Screenshot Directory.

---

## File Manager

The File Manager (Connection → File Manager) is a dual-pane file browser for transferring files between your PC and the Kronos over FTP.

| Pane | Contents |
|---|---|
| Left | Your local PC (starts at the Desktop folder) |
| Right | Kronos filesystem (`/` by default) |

It uses the same FTP credentials as the screen stream. Ensure the Kronos FTP service (vsftpd) is running before opening the File Manager.

### Transferring Files

- **Upload**: drag files from the left pane to the right, or select and click the upload toolbar button.
- **Download**: drag files from the right pane to the left, or select and click the download toolbar button.
- When a conflict occurs (file already exists), a dialog offers **Rename**, **Overwrite**, **Skip**, or **Cancel**, with an option to apply the choice to all remaining conflicts.

### Navigation and File Operations

| Key / Action | Effect |
|---|---|
| Double-click a folder | Navigate into it |
| Backspace | Go up to the parent folder |
| Hover over a folder (750 ms) | Navigate into it (dwell-to-navigate) |
| F5 | Refresh the active pane |
| F2 | Rename the selected item |
| Del | Delete the selected item (confirmation required) |
| Ctrl+A | Select all items in the active pane |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste within a pane |
| Toolbar - New Folder | Create a folder in either pane |
| Column header click | Sort by Name, Size, or Modified date |
| Drive selector (local pane) | Switch between drives on your PC |

Rubber-band selection (click-drag on empty space) and drag-scroll (drag to the top or bottom edge of a pane) are supported.

---

## Calibration

The calibration system corrects for systematic touch-position offsets between what is displayed in the client window and where the Kronos registers the touch.

Press `C` (default) to enter calibration mode. In this mode:
- A 5×5 grid of calibration nodes is overlaid on the frame.
- Drag any node to offset it from its natural (evenly-spaced) position.
- Optionally, add "bias dots" - extra test points - by clicking on the frame outside a node.

Calibration applies a bilinear mesh warp: client coordinates are mapped through the mesh before being sent as touch events. If all nodes are at their natural positions (zero offset), no correction is applied.

Changes take effect immediately and are saved automatically.

**Reset calibration:** Drag all nodes back to their natural positions, or use Settings → Reset All Settings (this resets everything, not just calibration).

---

## Test Mode

Access via **Tools → Enter Kronos Test Mode**. This sends the Kronos into its built-in hardware test mode for diagnostics and hardware verification.

> **Warning:** All unsaved changes on the Kronos will be lost, and the Kronos must be restarted after testing is complete. A confirmation dialog warns before proceeding. Only use this if you understand the risk.

---

## Macros

Macros record a sequence of key presses and replays them on demand. They are assigned a keyboard trigger that fires them globally while the app is in focus.

### Creating a Macro

1. Open Settings → Macros.
2. Click **Add**.
3. Enter a description.
4. Click **Trigger** and press the key combination you want to trigger the macro (must include at least one modifier: Ctrl, Alt, or Shift).
5. Click **● Record** and press the keys for your macro sequence.
6. Click **■ Stop** when done.
7. Adjust **Step delay** if needed (default 50 ms between steps).
8. Click **Play** to test.
9. Click OK.

### Macro Triggers

Triggers require a modifier key. Single keys without a modifier are not accepted as triggers (to avoid conflicting with normal keyboard use).

### Step Delay

The delay between macro steps is configurable per-macro (10–2000 ms). A higher delay gives slower Kronos UIs more time to process each step.

---

## Raw Key Map

The raw key map lets you bind a host key (optionally with Shift) to a specific Linux keycode sent to the Kronos, bypassing the normal character-map translation.

This is useful for:
- Triggering Kronos functions that do not have a named button or standard key mapping.
- Remapping keys that Eva interprets differently from what the standard map produces.
- Sending key combos that do not exist on the host keyboard layout.

Open Settings → Raw Key Map.

| Column | Meaning |
|---|---|
| Host Key | The Windows key to intercept (with optional Shift) |
| Raw Code | The Linux keycode (1–767) to send to the Kronos |
| Shift | Whether to send Shift along with the raw code |
| Label | Optional description |

**Adding a mapping:**
1. Click **Add** (or double-click an existing entry to edit).
2. Click **Host key** and press the key to capture.
3. Enter the raw Linux keycode.
4. Check **Send Shift** if needed.
5. Click **Save**.

**Keys that cannot be raw-mapped:** Numpad 0–9, numpad minus, and numpad decimal are reserved for physical button routing and cannot be remapped. The UI will reject them.

---

## Settings

Open via **Settings → Settings...**.

### Connection

| Setting | Description |
|---|---|
| Kronos Host | IP address or hostname of the Kronos |
| UDP Discovery | The app probes the daemon's UDP discovery port to find the stream/control ports automatically (falls back to the configured 7373/7374) |
| Stream Port | TCP port for the framebuffer stream (default 7373) |
| Control Port | TCP port for the control channel (default 7374) |
| FTP Port | TCP port for FTP file access used by the File Manager (default 21) |
| Username / Password | FTP credentials - used for both the screen stream and the File Manager. Set via the login dialog on first connect. |

### Streaming

| Setting | Description |
|---|---|
| Mode | **Change** (server pushes on change) or **Pull** (client polls at set FPS) |
| Max FPS | Maximum frame rate (1–15). Change mode caps the delivery rate; Pull mode sets the polling interval. |

**Change mode** (default) uses less bandwidth and CPU and is recommended for most use. The server only sends a frame when the Kronos screen content changes.

**Pull mode** sends frames at the fixed FPS regardless of whether the screen changed.

### General

| Setting | Description |
|---|---|
| Prompt before quitting | Show a confirmation dialog before closing the app |
| Hide Controls by default | Start with the button rail hidden |
| Screenshot Directory | Folder where Ctrl+S saves PNG screenshots |

### VGA Output

| Setting | Description |
|---|---|
| Enable VGA mirror on connect | Automatically send `MIRROR_ON` when connected |
| Screensaver timeout | Seconds of inactivity before blanking the VGA output (0 = disabled) |

### View

| Setting | Description |
|---|---|
| Layout Preset | Full / Focused (see [Layout Presets](#layout-presets)) |
| Zoom default level | Magnification level for the zoom window (2.5×–10×) |
| Zoom window size | Size multiplier for the floating zoom window |
| Always on top | Keep the main window above all other windows |

### Key Bindings

All rebindable actions are listed. Double-click any row to capture a new key combination. Press Escape to cancel; press Delete to clear a binding.

**Rebindable actions:**

- Quit, Toggle Fullscreen, Toggle Zoom Window, Zoom In, Zoom Out
- Toggle Aspect Lock, Toggle VGA Mirror, Toggle Help
- Toggle Calibration Mode, Hide/Show Controls
- Mode: Setlist / Combi / Program / Sequence / Sampling / Global / Disk
- Bank: I-A through I-G, U-A through U-G, U-AA through U-GG

### MIDI/SysEx

| Setting | Description |
|---|---|
| MIDI transport | **Auto** (USB if present, else TCP) / **USB** / **TCP** |
| USB device name | Device-name substring used to find the Kronos USB-MIDI port (default `KRONOS`) |
| Monitor incoming MIDI | Connect/read the live MIDI stream (needed for follow, the monitor, and dumps) |
| Proactive SysEx polling | Periodically query the current performance id even when no change was seen |
| SysEx poll interval | Seconds between proactive polls (default 60) |
| Value slider CC# | MIDI controller number the Kronos VALUE slider transmits (default 18) |

### Librarian

| Setting | Description |
|---|---|
| Merge behavior | **Temporary Memory** (staging cleared on close) or **Local Storage** (persisted across sessions) |

### Macros

See [Macros](#macros).

### Debug

| Setting | Description |
|---|---|
| Debug logging | Write verbose connection and frame diagnostics to the application log |

### Import / Export / Reset

- **Export** - save all settings (including key bindings, macros, and raw key map) to a JSON file.
- **Import** - load settings from a previously exported JSON file.
- **Reset All Settings** - permanently deletes all saved settings, key mappings, calibration data, and palette overrides. The app returns to its out-of-the-box state.

---

## MIDI / SysEx

The app monitors the Kronos' live MIDI output - program-change and mode-change follow, the VALUE-slider mirror, and the SysEx/MIDI traffic window all run off it.

### Transport selection

The MIDI link is chosen in **Settings → MIDI/SysEx** (default **Auto**):

- **USB** - a Kronos directly connected via USB-MIDI (no daemon/network needed). Fast and exclusive - if a DAW already holds the port, the app falls back to TCP.
- **TCP** - the daemon's MIDI bridge (port 9875), used when the screen is connected.

The footer badge shows which link is live: **USB** (green, fast), **DIN** (amber, a 5-pin interface bridging the Kronos), **TCP** (blue, network), or **-** (none). Hot-plug is automatic - plugging in a USB Kronos switches over live.

### SysEx / MIDI Monitor (Tools → SysEx/MIDI Monitor)

- Live traffic log with per-message decode (notes, CC, PC, SysEx function codes).
- Send raw MIDI hex.
- **Sync Names** - dumps every program/combi name bank once and caches it, so program-change display is flash-free (a bank is re-dumped only after its storage digest changes).
- **Set List sync** - dumps all set lists into a local cache.

> **Note:** SysEx receive ("Enable Exclusive") must be on in the Kronos Global/MIDI settings for the monitor, name sync, and set-list features to work. If a DAW is running, it may hold the USB port - the app auto-falls-back to TCP.

---

## Librarian (Tools → Librarian...)

The Librarian is a full library manager for Kronos programs, combis, and set lists: it can pull the whole instrument into a local library, import `.pcg` files, stage objects in a Merge Window, and place them back onto the Kronos with dependency resolution. Everything is undoable (Ctrl+Z) and every write to the Kronos is preceded by a backup.

### PCG pane (left)

Load a `.pcg` file to browse its banks. Pull objects into the Merge Window (right-click or drag):

- Pulling a **set list** transitively pulls the combis it references, which in turn pull their programs.
- Pulling a **combi** pulls its programs.
- References that don't resolve inside the loaded file are reported as gaps - they stay unresolved until a file that does contain them is loaded and pulled.
- The search box above the tree matches name, bank (e.g. `I-A`), category, EXi engine type (e.g. `AL-1` matches both a name containing it and a program that *is* one), and what the object itself references. Case-insensitive.
- Loading a `.pcg` also resolves EXs/3rd-party **sample bank names** from the shipped EXs catalog (a local read - no connection, no login). A catalog hit identifies the product; it is not proof the pack is installed on the instrument.

### Local Library (center)

- **Sync Library** pulls every program/combi/set list from the Kronos into the on-disk local library (content-addressed, with per-bank SHA-1 digests).
- Cut/copy/paste to rearrange within the library, including whole-bank moves (Program banks can be copied across an EXi/HD-1 boundary, which stages a bank-type reformat for the next Commit).
- **Commit** writes pending edits to the Kronos and issues the Store-Bank step. Conflicts (the bank changed on the Kronos since baseline) are flagged, never silently overwritten.
- Read-only factory banks (GM, g1–g9, gd) are browseable but never writable.

### Merge Window (right)

A staging area between a loaded `.pcg` file / the local library and the instrument:

- Stage objects from the PCG pane or from Local Library ("Move to Merge Window").
- **Auto-Fill** places everything staged into the next free slots of the correct type - dependencies are placed before their referrers, so a combi's timbres point at where its programs actually landed. Placement follows the Merge Window's own display order (source bank, then slot), so re-copying the same `.pcg` and auto-filling again lands in the same order every time.
- Auto-Fill sends nothing to the Kronos - it only stages, exactly like dragging items across yourself. Review the result, then **Commit Changes** to push. Anything that doesn't fit (no bank of the matching type has room) stays staged.
- Placing is address-sensitive (you choose the destination); dependencies are resolved automatically.
- **Force Overwrite**: placing onto a slot another combi or set list still references normally refuses, to avoid silently breaking that reference. Force Overwrite places anyway - those referrers then resolve to the *new* object, and the old occupant is diverted to the session clipboard rather than lost.
- **Object Dependencies** (bottom right): red rows at the top are dependencies nothing staged provides. Right-click one to search a `.pcg` for it; anything found is staged, so the gap can be filled before you Commit. Below them is every program/combi/drum kit/wave sequence the selection references, nested ones included - double-click a row for more info.
- Tree dots and tints: a dot marks an object staged or referenced more than once, a blue dot marks a sample reference (legend at the bottom of the window), and a conflicted, pending-delete, or read-only row is tinted instead of dotted.
- **Preserve duplicate Programs/Combis** (Merge Window toolbar, mirrored in Settings → Librarian): when checked, placing staged content that already exists in Local Library still writes a fresh copy ("preserve duplication"); when unchecked, the existing byte-identical copy is reused instead of consuming a slot. Combis are compared *after* their program references are re-pointed at local reality, so a re-copied chain still matches. Defaults: Programs reused (unchecked), Combis copied as-is (checked).
- **Merge behavior** (Settings → Librarian): **Temporary Memory** clears the staging when the app closes; **Local Storage** persists it across sessions.

---

## Mode Detection

The current Kronos operating mode is read from the daemon's **STATE** command (polled every 500 ms). The daemon reports it from Eva's own live state (via its `eva_mode` kernel module), falling back to its own framebuffer pixel detection during early boot - so the mode shown is exact and doesn't depend on client-side image matching.

- The mode is shown in the status bar and drives the lit mode button on the control surface.
- Mode change buttons are ignored until the daemon confirms the board has finished booting (its `BOOT=` gate), so a stray press during boot can't light the wrong button.
- The daemon also reports a program-edit context (`EDITCTX`), which lights the Combi or Sequence button while you're editing a Program from inside one.

(The client still uses a reference-image match purely for the on-screen Help overlay.)

---

## Input Tester

The input tester (Settings → Raw Key Map → **Input Tester** button) lets you see exactly which keycodes the client sends for any key press, before it reaches the Kronos. Useful for diagnosing unexpected mappings or verifying raw key map entries.

---

## Status Bar

The status bar at the bottom of the window shows:

| Element | Description |
|---|---|
| Coloured dot + text | Connection state: green = connected, amber = connecting, gray = disconnected |
| ⌨ keyboard icon | Keyboard capture state - right-click to enable/disable keyboard send |
| FPS | Measured incoming frame rate while connected |
| Latency | Round-trip network latency to the Kronos |
| Notification bubble | Click to open the log file; turns red on errors |
| Keyboard Info | Opens a pane displaying CPU, memory, temperature, and storage stats |
| VU meter | Audio level of a local Windows device (e.g. your DAW output). Click ▾ to pick the device; choice is saved |
| MIDI link badge | USB (green) / DIN (amber) / TCP (blue) - which link carries MIDI/SysEx (see [MIDI / SysEx](#midi--sysex)) |
| Change / Pull | Active streaming mode for the current connection |
| Mode | Current Kronos operating mode - right-click to change mode |

---

## Tray Icon

The app minimises to the system tray when the window is closed with **Minimize to tray** behaviour. Right-click the tray icon to restore the window or quit.

---

## Keyboard Shortcut Reference

| Key | Action |
|---|---|
| `F1` | Open help window |
| `F2`–`F8` | Switch Kronos mode (Setlist through Disk) |
| `C` | Toggle calibration mode |
| `F` | Toggle fullscreen |
| `M` | Toggle VGA mirror |
| `Q` | Quit |
| `Z` | Toggle zoom window |
| `+` / `−` | Zoom in / zoom out (enables zoom automatically if off) |
| `Esc` | Send EXIT to Kronos / exit fullscreen / dismiss overlays |
| `Enter` | Send ENTER to Kronos |
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
  raw_key_mappings.json    - raw key map entries
  macros.json              - recorded macro sequences
  cal_data.json            - calibration mesh
  palette_override.json    - palette overrides
  screenremote.log         - diagnostic log (written when Debug Logging is enabled)

  local_library/           - the Librarian's local library (index.json, oplog.jsonl, content-addressable blobs)
  name_cache.json          - per-Kronos program/combi name cache (program-change follow)
  setlist_cache.json       - per-Kronos set-list cache
  dumped_banks.json        - per-Kronos ledger of name banks already dumped
  category_names_cache.json - per-Kronos category names (from a Global dump)
  librarian_backups/       - timestamped .syx backups taken before every Kronos write
  local_library_clipboard.json - the Librarian's cross-session clipboard
```

All files are JSON (the local library uses JSON + append-only op-log + SHA-1-addressed blobs) and can be hand-edited. The **Export/Import** feature in Settings backs up/restores `settings.json` (including key bindings and macros); the local library and caches are separate.

> **Note:** the local library and caches are keyed per Kronos (by host for TCP, by device match for USB), so reconnecting to the same instrument reuses them.

---

## Troubleshooting

**Login dialog appears on every connect / "Authentication Failed":**
- Saved credentials are wrong or have changed. Clear them in Settings → Connection (Username/Password) and try again.
- The Kronos FTP service (vsftpd) must be running - check with `ps | grep vsftpd` via SSH.
- Credentials can be tested from a command line: `ftp 192.168.1.2` (or your Kronos IP).
- If using KronosNet.conf, ensure the file at `/korg/rw/screenremote/KronosNet.conf` is readable and correctly formatted (`username:password`, one entry per line).
- **Note:** the daemon also accepts the username `kronos` with the device's PublicID as a password (an emergency recovery path for screen connect).

**Connection times out after 10 seconds:**
- Check that the Kronos is powered on and the `screenremote` daemon is running (`ps | grep screenremote` via SSH).
- Verify the IP address and port (use UDP discovery or check the daemon's stderr output in dmesg).
- Check Windows Firewall - it may block outbound connections to port 7373.
- Some antivirus/VPN drivers intercept socket I/O and delay connections. The 10-second watchdog is implemented by closing the socket, which bypasses this reliably.

**Screen appears but touch does nothing:**
- Confirm the control port (7374) is reachable - try `telnet 192.168.1.2 7374`.
- Check that the stream client connected first (access control: only the stream client IP can send control commands).
- If using a VPN or multiple network interfaces, the daemon may have bound to a different LAN IP. Check `dmesg | grep screenremote` on the Kronos for the bound address.
- The daemon may still be in its boot gate (BOOT=1) - mutating commands return `ERR BOOTING` until it clears.

**Keys do not produce the expected characters:**
- Eva's character mapping is inverted from standard keyboards: unshifted keys produce uppercase, shifted keys produce lowercase. This is intentional.
- Caps Lock is emulated by injecting Left Shift - if a letter is stuck lowercase, check the keyboard status (⌨ indicator) and that Shift was released.
- Check the raw key map (Settings → Raw Key Map) for conflicting entries.
- Numpad keys route to physical buttons and cannot be used for text input.

**Frame is frozen / no updates:**
- Send `REFRESH` via a one-shot control connection to force a full frame resend.
- In Change mode, if the Kronos screen is genuinely static, no frames are sent - this is normal.

**VGA mirror turns off after a while:**
- The screensaver timeout has fired. Reduce or disable it in Settings → VGA Output → Screensaver timeout.

**Mode is shown as "Unknown" / mode buttons don't light:**
- The daemon's boot gate is still closed (BOOT=1) - the daemon refuses mutating commands and reports mode as 0 until the board has finished booting. Wait a few seconds after power-on; the app disables mode presses until it clears.
- The daemon's `eva_mode` module couldn't resolve (check MODE_DETAIL on the daemon).
- You're not connected (mode follows the daemon's STATE poll, which needs a connection).
