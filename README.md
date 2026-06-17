# KronosScreenRemote for Windows

A Windows desktop application for remotely viewing and controlling a **Korg Kronos** synthesizer over Ethernet. It streams the Kronos display in real time, forwards touch/button input back to the device, and provides supplementary tools for audio monitoring, file management, and display calibration.

> **Note:** This application requires the companion daemon running on the Kronos hardware.
> See [KronosScreenRemoteDaemon](https://github.com/Enigmahack/KronosScreenRemoteDaemon) for setup instructions.

| Repository | Description |
|---|---|
| [KronosScreenRemote](https://github.com/Enigmahack/KronosScreenRemote) | This repo — Windows desktop client |
| [KronosScreenRemoteDaemon](https://github.com/Enigmahack/KronosScreenRemoteDaemon) | Kronos-side daemon (required) |

---

## Features

- **Live Screen Streaming** — 800×600 8-bit indexed color at up to 15 FPS via TCP; supports full-frame (pull) and change-only modes for bandwidth efficiency
<img width="1202" height="504" alt="image" src="https://github.com/user-attachments/assets/255cdc73-ed24-4f39-a707-297bca95fdd5" />

- **Remote Control** — Virtual button panel (mode keys, number pad, data wheel, bank selects) with drag, scroll, and keyboard-shortcut support
<img width="1202" height="504" alt="image" src="https://github.com/user-attachments/assets/2c867bcd-60e4-423c-86d5-fe062720e055" />

- **Touch Calibration** — 3x3 - 5x5 warp mesh with bilinear interpolation for accurate touch-to-screen mapping
<img width="1202" height="504" alt="image" src="https://github.com/user-attachments/assets/103f3f42-e299-420c-b968-6a1d47f543e8" />

- **Mode Detection** — Reference-image OCR to identify the active Kronos operating mode automatically
- **Audio VU Meter** — WASAPI real-time level monitoring (L/R peak + RMS) with device selection
<img width="1202" height="504" alt="image" src="https://github.com/user-attachments/assets/659cabac-e044-4663-9431-724142d6ef14" />

- **FTP File Manager** — Browse, upload, and download files on the Kronos SD card with conflict resolution
<img width="1203" height="505" alt="image" src="https://github.com/user-attachments/assets/86ef3f27-5bc0-4741-b285-72347d6a063e" />

- **Command Palette** — Searchable keyboard-driven command interface (Ctrl+K)
<img width="1203" height="505" alt="image" src="https://github.com/user-attachments/assets/8a416c12-6568-46d2-aa94-d4178634830f" />

- **Zoom & Layout Presets** — Configurable window sizes (75–200%), fullscreen, always-on-top, and collapsible control rail
<img width="1203" height="505" alt="image" src="https://github.com/user-attachments/assets/58331f2a-2183-4721-aa0f-d89767f6b6bf" />

- **Hardware Stats Monitoring** — Monitor hard drive space, CPU core usage, Fan speed, CPU temperatures, and more.
<img width="1202" height="504" alt="image" src="https://github.com/user-attachments/assets/1bc59ac4-2149-4535-8d2f-ee75af76dcee" />


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
├── Core/           # Logging, settings, models, and JSON persistence
├── Detection/      # OCR-based mode detection and boot phase tracking
├── Networking/     # TCP stream receiver, control client, FTP layer
├── Rendering/      # Overlay, palette, and button rendering helpers
├── Views/          # WPF windows and XAML (MainWindow, FileManager, dialogs)
├── Resources/      # Icons, button images, calibration reference data
├── Documentation/  # Extended documentation (architecture, protocols, etc.)
├── sign.ps1        # Code-signing helper script
└── KronosScreenRemote.sln
```

`MainWindow` is split across ~10 partial classes (`MainWindow*.cs`) covering streaming, input, audio, calibration, the palette editor, and general UI state.

---

## Connecting to a Kronos

1. Ensure the Kronos is connected to your local network and its **Global > Ethernet** settings have a valid IP address.
2. Launch **KronosScreenRemote** and enter the Kronos IP in the connection bar.
3. The application connects on **TCP 7373** (screen stream) and **TCP 7374** (control commands).
4. FTP access (file manager) uses the standard FTP port **21** with the credentials configured on the Kronos.

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+K | Open command palette |
| F1–F8 | Switch Kronos operating mode |
| Ctrl+1–5 | Window size preset (75%–200%) |
| C | Toggle calibration grid overlay |
| W | Enter warp/mesh editing mode |
| F | Toggle fullscreen |

Shortcuts are rebindable via **Settings → Keybinds**.

---

## License

All rights reserved. This source code is provided for reference purposes only.

---

## Contributing

Issues and pull requests are welcome. Please open an issue first for any significant change so the approach can be discussed before implementation.
