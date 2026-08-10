# KronosScreenRemote for Windows

A Windows desktop application for remotely viewing and controlling a **Korg Kronos** synthesizer over Ethernet. It streams the Kronos display in real time, forwards touch/button input back to the device, and provides supplementary tools for MIDI/SysEx integration, audio monitoring, file management, and display calibration.

*AI DISCLAIMER*
This application has not been vibe-coded - Some AI was used in the development of this application in terms of comments, notes, and tests, but the majority of this code has had human oversight. 

> **Note:** This application requires the companion daemon running on the Kronos hardware.
> See [KronosScreenRemoteDaemon](https://github.com/Enigmahack/KronosScreenRemoteDaemon) for setup instructions.

| Repository | Description |
|---|---|
| [KronosScreenRemote](https://github.com/Enigmahack/KronosScreenRemote) | This repo - Windows desktop client |
| [KronosScreenRemoteDaemon](https://github.com/Enigmahack/KronosScreenRemoteDaemon) | Kronos-side daemon (required) |

> **Shared context**: this project is part of a larger Kronos RE/modding
> ecosystem (this client + [KronosScreenRemotePy](../KronosScreenRemotePy/) +
> [kronosology](../kronosology/) + [KronosScreenRemoteDaemon](../KronosScreenRemoteDaemon/)).
> Cross-project architecture, shared dev environments, credentials/access
> pointers, and agent/tooling policy live in
> [`/home/share/PROJECT_BRAIN/BRAIN.md`](../PROJECT_BRAIN/BRAIN.md) -
> check there before duplicating knowledge into this repo.

---

## Features

- **Live Screen Streaming** - 800×600 8-bit indexed color at up to 15 FPS via TCP; supports full-frame (pull) and change-only modes for bandwidth efficiency
- **Value Slider** - Left-panel INC/DEC buttons and draggable 0–127 value slider mirroring the Kronos front-panel VALUE control; double-click to snap to center (64)
- **Remote Control** - Virtual button panel (mode keys, number pad, data wheel, bank selects) with drag, scroll, and keyboard-shortcut support
- **Mode Detection** - The current Kronos operating mode is read from the daemon's STATE command (exact, from Eva's own state via the daemon's `eva_mode` module), with boot-gating so a stray press during boot can't light the wrong button
- **MIDI / SysEx Integration** - Live MIDI-out monitoring with a SysEx/MIDI traffic window; automatic program-change follow, mode follow, and VALUE-slider mirroring from the hardware. Runs over the daemon's TCP MIDI bridge (port 9875) or a direct USB-MIDI link (selectable in Settings → MIDI/SysEx; Auto prefers USB, with live hot-plug)
- **Librarian** - Full library manager: sync programs/combis/set lists to an on-disk local library, import `.pcg` files, stage objects in a Merge Window, and place them back onto the Kronos with transitive dependency resolution and undo
- **Set List & Name Tools** - Dump and browse Kronos Set Lists, and sync program/combi names into a per-Kronos cache for flash-free program-change display ("Sync All" collects both)
- **Audio VU Meter** - WASAPI real-time level monitoring (L/R peak + RMS) with device selection
<img width="1414" height="508" alt="2026-06-19 17_44_49-Kronos ValueSlider - 192 168 100 15" src="https://github.com/user-attachments/assets/fa7ad681-8056-489f-99e8-32f90af12e98" />

- **Touch Calibration** - 3x3 - 5x5 warp mesh with bilinear interpolation for accurate touch-to-screen mapping
<img width="1414" height="508" alt="2026-06-19 17_39_29-Kronos ScreenRemote - 192 168 100 15" src="https://github.com/user-attachments/assets/ebe0858e-67c6-4a45-a5fa-85590d2bbdba" />

- **FTP File Manager** - Browse, upload, and download files on the Kronos SD card with conflict resolution
<img width="1414" height="508" alt="2026-06-19 17_40_35-File Manager - Kronos" src="https://github.com/user-attachments/assets/82fc2864-1e3e-4553-a1de-4708fd746a75" />

- **Test Mode** - Enter the Kronos built-in hardware test mode for diagnostics (Tools menu)
- **Portable Settings** - Preferences, key bindings, and macros persist as JSON; export or import your full configuration via **File → Export/Import Settings...**
- **Zoom & Layout Presets** - Configurable window sizes (75–200%), fullscreen, always-on-top; data input (right) and value input (left) panels can be independently hidden in Full mode or expanded/collapsed via dedicated rails in Focused mode, with panel state remembered across sessions
<img width="904" height="508" alt="2026-06-19 17_45_52-_VariousViews" src="https://github.com/user-attachments/assets/92c41123-f285-46db-a83f-30e131370ec3" />

- **Hardware Stats Monitoring** - Monitor hard drive space, CPU core usage, Fan speed, CPU temperatures, and more.
<img width="1414" height="508" alt="2026-06-19 17_47_52-Kronos ScreenRemote Keyboard Info - 192 168 100 15" src="https://github.com/user-attachments/assets/13a83afc-056a-4189-87b5-ea05dd181d3a" />

---

## Requirements

### Runtime

| Requirement | Minimum |
|---|---|
| OS | Windows 10 (x64) or Windows 11 |
| .NET Runtime | .NET 10 Desktop Runtime (Windows) |

### Build

| Requirement | Version |
|---|---|
| .NET SDK | 10.0 |
| OS | Windows 10/11 (WPF is Windows-only) |
| IDE (optional) | Visual Studio 2022 v17.12+ or JetBrains Rider 2024.3+ |

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| [FluentFTP](https://github.com/robinrodricks/FluentFTP) | 51.0.0 | FTP client for Kronos SD card file manager |
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | WASAPI audio capture for the VU meter |

Dependencies are restored automatically by NuGet during build.

---

## Building

```powershell
# Clone the repository
git clone https://github.com/Enigmahack/KronosScreenRemote.git
cd KronosScreenRemote

# Restore packages and build
dotnet build KronosScreenRemote.csproj

# Publish a self-contained single-file executable (x64)
dotnet publish -p:PublishProfile=win-x64
```

The published executable will appear in `bin\Release\net10.0-windows\win-x64\publish\`.

### Code Signing (optional)

A PowerShell helper script is included for self-signed or CA-signed code signing:

```powershell
# First-time setup: generate a self-signed certificate
.\sign.ps1 -Setup

# Sign the built executable
.\sign.ps1
```

---

## Project Structure

```
KronosScreenRemote/
├── Audio/          # WASAPI audio capture and VU meter engine
├── Core/           # Logging, settings, models, JSON persistence, local library & PCG
├── Detection/      # Help-overlay detection
├── Networking/     # Stream receiver, control client, FTP, MIDI/SysEx transports
├── Rendering/      # Overlay, palette, and button rendering helpers
├── ViewModels/     # Librarian / Merge / Pane view-models and their self-tests
├── Views/          # WPF windows and XAML (MainWindow, LibrarianShell, FileManager, dialogs)
├── Resources/      # Icons, button images, calibration reference data
├── Documentation/  # Extended documentation (user guide, daemon API reference)
├── sign.ps1        # Code-signing helper script
└── KronosScreenRemote.sln
```

`MainWindow` is split across partial classes (`MainWindow*.cs`) covering streaming, input, audio, calibration, and general UI state. The Librarian lives in `ViewModels/LibrarianShellViewModel.cs` + `Views/LibrarianShellWindow.xaml` with an extensive off-hardware self-test suite (`App.xaml.cs` `--librarian-selftest`).

---

## Connecting to a Kronos

1. Ensure the Kronos is connected to your local network and its **Global > Ethernet** settings have a valid IP address.
2. Launch **KronosScreenRemote** and enter the Kronos IP in the connection bar (the app probes UDP discovery to find the daemon's ports automatically).
3. The application connects on **TCP 7373** (screen stream) and **TCP 7374** (control commands). MIDI/SysEx monitoring uses the daemon's internal bridge on **TCP 9875**, or a direct USB-MIDI connection (Auto prefers USB when a Kronos is plugged in).
4. FTP access (file manager) uses the standard FTP port **21** with the credentials configured on the Kronos.
5. The **Librarian** (Tools → Librarian...) additionally syncs programs/combis/set lists from the Kronos into a local library (see the [user guide](Documentation/KronosScreenRemote_Guide.md)).

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| F1 | Open help window |
| F2–F8 | Switch Kronos operating mode (Setlist through Disk) |
| C | Toggle calibration mode |
| F | Toggle fullscreen |
| M | Toggle VGA mirror |
| Q | Quit |
| Z | Toggle zoom window |
| Ctrl+1–5 | Window size preset (75%–200%) |
| Ctrl+K | Open command palette |
| Ctrl+S | Save screenshot |

Shortcuts are rebindable via **File → Settings... → Key Bindings**.

---

## License

All rights reserved. This source code is provided for reference purposes only.

---

## Contributing

Issues and pull requests are welcome. Please open an issue first for any significant change so the approach can be discussed before implementation.
