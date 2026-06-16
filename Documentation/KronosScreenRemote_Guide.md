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
- "TCP connected - sending handshake…"
- "Handshake OK - 800×600"

If credentials are wrong, an "Authentication Failed" message appears. If the connection times out after 10 seconds, a message lists likely causes: firewall blocking port 7373, daemon not running, or cable unplugged.

### 4. Once Connected

The Kronos screen is displayed live in the main window. The title bar shows the current mode (Setlist, Combi, Program, etc.) detected from the screen content.

---

## Interacting with the Kronos

### Touch Input

Click anywhere on the displayed screen to send a tap to the Kronos at the corresponding position. The coordinates are scaled from the window display size back to the native 800×600 framebuffer space automatically.

- **Click** - tap (press + release)
- **Click and drag** - pen-down, move, pen-up (swipe or drag gestures)

If touch calibration has been set up (see [Calibration](#calibration)), coordinates are corrected before being sent.

### Front-Panel Buttons

Physical buttons on the Kronos front panel can be triggered from the client. Buttons appear in the **Buttons** panel at the right side of the window (visible in Full and Focused layout modes).

Click any button in the panel to trigger it. The panel groups buttons by function:

| Group | Buttons |
|---|---|
| Mode | Setlist, Combi, Program, Sequence, Sampling, Global, Disk |
| Navigation | Exit, Enter |
| Utility | Help, Compare, Reset |
| Number pad | 0–9, Dot, Dash |
| Value | INC, DEC |
| Mix Play | MP1–MP8 |
| Mix Select | MS1–MS8 |
| Bank - Internal | I-A through I-G |
| Bank - User | U-A through U-G |
| Sequencer | Start, Rec, Locate, FF, Rew, Pause, Tap Tempo |
| Sampling | Rec, Start |
| Channel strip | Mix Knobs, Solo |

Not all buttons are currently implemented in the Kronos Screen Remote software; some are intended for live control and are not suitable for use with a mouse or remote interface. 

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

### Aspect Lock

The frame maintains the native 800×600 aspect ratio by default. Press `A` to toggle aspect lock. When unlocked, the frame stretches to fill the window.

### Fullscreen

Press `F` (default) to toggle fullscreen. The title bar and controls are hidden in fullscreen; move the mouse to the top edge to reveal them temporarily.

### Hide Controls

Press the collapse button on the button rail to hide the right-side control panel. Reassignable in Settings.

### Layout Presets

Three layout presets are available (Settings → View → Layout Preset):

| Preset | Description |
|---|---|
| **Full** | Frame + button rail + all controls visible |
| **Focused** | Button rail collapsed by default; expands on hover |
| **Detached** | Frame only; buttons/controls in a separate floating window |


---

## VGA Mirror

When enabled, the daemon also copies the Kronos screen to the VGA output (`/dev/fb0`). This lets you connect an external monitor directly to the Kronos VGA port.

Toggle via **View → VGA Mirror** or the `M` key (default), or enable it by default in Settings → VGA Output → Enable on connect.

### Screensaver

When the VGA mirror is active and the Kronos screen has been static for the screensaver timeout period, the VGA output is blanked automatically. It wakes on any screen change. The timeout is configurable (Settings → VGA Output → Screensaver timeout; 0 to disable). It can also be changed at runtime from the control port.

---

## Screenshots

Press `Ctrl+S` to save a screenshot of the current frame as a PNG file. The output directory is set in Settings → General → Screenshot Directory. If no directory is set, a Save As dialog appears.

---

## File Manager

The File Manager (Tools → File Manager) is a dual-pane file browser for transferring files between your PC and the Kronos over FTP.

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

The delay between macro steps is configurable per-macro (5–500 ms). A higher delay gives slower Kronos UIs more time to process each step.

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

Open via **Edit → Settings** (or the gear icon).

### Connection

| Setting | Description |
|---|---|
| Kronos Host | IP address or hostname of the Kronos |
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
| Layout Preset | Full / Focused / Detached (see [Layout Presets](#layout-presets)) |
| Zoom default level | Magnification level for the zoom window (1×–5×) |
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

## Mode Detection

The client automatically identifies which Kronos mode is active by comparing the top-left corner of each received frame against reference images. The detected mode is shown in the title bar and status bar.

Detection uses embedded PNG reference images for each of the 7 modes and the help screen. A mode is declared when 85% or more of the reference mask pixels match within a colour tolerance of ±30 per channel. The help-screen overlay requires 97% match.

If the reference images are missing (unlikely in a normal build), the mode display shows "Unknown".

---

## Input Tester

The input tester (Settings → Raw Key Map → **Input Tester** button) lets you see exactly which keycodes the client sends for any key press, before it reaches the Kronos. Useful for diagnosing unexpected mappings or verifying raw key map entries.

---

## Status Bar

The status bar at the bottom of the window shows:
- Connection state (Connecting / Connected / Disconnected)
- Current Kronos mode
- Frame dimensions
- FPS indicator (when connected)
- VU meter - shows the level of a selected local Windows audio device (click the ▲ picker button to choose a device; choice is saved in settings)
- Error messages from the daemon (ERR responses on the persistent control connection)

---

## Tray Icon

The app minimises to the system tray when the window is closed with **Minimize to tray** behaviour. Right-click the tray icon to restore the window or quit.

---

## Keyboard Shortcut Reference

| Key | Action |
|---|---|
| `F` | Toggle fullscreen |
| `Z` | Toggle zoom window |
| `A` | Toggle aspect lock |
| `M` | Toggle VGA mirror |
| `C` | Toggle calibration mode |
| `F1` | Toggle help overlay |
| `Q` | Quit |
| `F2`–`F8` | Switch Kronos mode (Setlist–Disk) |
| `Ctrl+Z` | Undo (in palette editor) |
| `Ctrl+Y` | Redo (in palette editor) |
| `Ctrl+S` | Save screenshot |
| Mouse wheel | Zoom in/out (over frame) or data wheel (assigned to Kronos) |

All shortcuts except Ctrl+S, Ctrl+Z, and Ctrl+Y are rebindable in Settings → Key Bindings.

---

## Data Files

Application data is stored in:

```
%APPDATA%\KronosScreenRemote\
  settings.json           - connection, streaming, display settings, key bindings, credentials
  raw_key_mappings.json   - raw key map entries
  macros.json             - recorded macro sequences
  cal_data.json           - calibration mesh
  palette_override.json   - palette overrides
  palette_lock.json       - palette lock state
  screenremote.log        - verbose diagnostic log (written when Debug Logging is enabled)
```

All files are JSON and can be hand-edited. The **Export/Import** feature in Settings is the supported way to back them up or transfer them to another machine.

---

## Troubleshooting

**Login dialog appears on every connect / "Authentication Failed":**
- Saved credentials are wrong or have changed. Clear them in Settings → Connection (Username/Password) and try again.
- The Kronos FTP service (vsftpd) must be running - check with `ps | grep vsftpd` via SSH.
- Credentials can be tested from a command line: `ftp 192.168.1.2` (or your Kronos IP).
- If using KronosNet.conf, ensure the file at `/korg/rw/screenremote/KronosNet.conf` is readable and correctly formatted (`username:password`, one entry per line).

**Connection times out after 10 seconds:**
- Check that the Kronos is powered on and the `screenremote` daemon is running (`ps | grep screenremote` via SSH).
- Verify the IP address and port (use UDP discovery or check the daemon's stderr output in dmesg).
- Check Windows Firewall - it may block outbound connections to port 7373.
- Some antivirus/VPN drivers intercept socket I/O and delay connections. The 10-second watchdog is implemented by closing the socket, which bypasses this reliably.

**Screen appears but touch does nothing:**
- Confirm the control port (7374) is reachable - try `telnet 192.168.1.2 7374`.
- Check that the stream client connected first (access control: only the stream client IP can send control commands).
- If using a VPN or multiple network interfaces, the daemon may have bound to a different LAN IP. Check `dmesg | grep screenremote` on the Kronos for the bound address.

**Keys do not produce the expected characters:**
- Eva's character mapping is inverted from standard keyboards: unshifted keys produce uppercase, shifted keys produce lowercase. This is intentional.
- Check the raw key map (Settings → Raw Key Map) for conflicting entries.
- Numpad keys route to physical buttons and cannot be used for text input.

**Frame is frozen / no updates:**
- Send `REFRESH` via a one-shot control connection to force a full frame resend.
- In Change mode, if the Kronos screen is genuinely static, no frames are sent - this is normal.

**VGA mirror turns off after a while:**
- The screensaver timeout has fired. Reduce or disable it in Settings → VGA Output → Screensaver timeout.

**Mode is shown as "Unknown":**
- The reference PNG images are missing from the app bundle.
- The Kronos firmware version uses a slightly different UI layout. The tolerance (±30) covers minor differences; a major UI change would require new reference images.
