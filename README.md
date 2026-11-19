<p align="center">
  <img src="src/Ember/Assets/ember.png" width="96" alt="Ember icon">
</p>

<h1 align="center">Ember</h1>

<p align="center">
  A hardware temperature monitor for Windows and Linux, with an always-on-top desktop overlay and a live dashboard.
</p>

---

> [!NOTE]
> **Project status:** Ember is a personal project, shared as-is. It is updated when time allows, and
> issues and pull requests are welcome but may not always get a response.

## Features

- **Overlay**: a compact, always-on-top readout of CPU, GPU, RAM and drive temperatures, toggled with a
  global hotkey. Three styles (Pill, Card, Rings), adjustable size and opacity, and optional usage and
  power-draw readings. When locked, clicks pass straight through it.
- **Dashboard**: current, minimum and maximum readings with live graphs for each component.
- **All sensors**: every sensor the system reports (temperatures, loads, clocks, fans, voltages, power), grouped by device.
- **Alerts**: readings change colour at configurable thresholds, with optional desktop notifications
  when a component stays hot.
- **Tray icon**: can show the CPU temperature directly in the notification area.
- Celsius or Fahrenheit, multi-monitor support, start on sign-in, dark theme with a configurable accent colour.

Usage figures (CPU, GPU, RAM) are calculated the same way as Windows Task Manager, so the two agree.

## Supported platforms

| Platform | Requirements |
|---|---|
| Windows 10 / 11 (x64) | Administrator rights during installation. The [PawnIO](https://pawnio.eu) driver is required for CPU, RAM and motherboard sensors. |
| Linux (x64) | X11 or Wayland session. Packages for Debian/Ubuntu, Fedora/openSUSE and Arch, plus an AppImage. |

Builds are self-contained; .NET does not need to be installed to run Ember.

## Installation

### Windows

1. Download and run `Ember-Setup-<version>.exe`.
2. The installer is not code-signed, so SmartScreen may show *"Windows protected your PC"*. Select
   **More info → Run anyway**.
3. Keep **Install the PawnIO driver** selected. It downloads the signed driver from pawnio.eu.
   Without it, only GPU and drive temperatures are available.

Reading CPU and motherboard sensors requires administrator rights. The installer registers scheduled
tasks so Ember starts elevated without a UAC prompt, both at sign-in and when launched manually.

### Linux

| Distribution | Command |
|---|---|
| Debian, Ubuntu, Mint, Pop!_OS | `sudo apt install ./ember-monitor_<version>-1_amd64.deb` |
| Fedora, RHEL-based | `sudo dnf install ./ember-monitor-<version>-1.x86_64.rpm` |
| openSUSE | `sudo zypper install ./ember-monitor-<version>-1.x86_64.rpm` |
| Arch, Manjaro, EndeavourOS | `sudo pacman -U ./ember-monitor-<version>-1-x86_64.pkg.tar.zst` |
| Other | `chmod +x Ember-<version>-x86_64.AppImage && ./Ember-<version>-x86_64.AppImage` |

See [packaging/linux/README-linux.md](packaging/linux/README-linux.md) for hotkey setup on Wayland,
tray support on GNOME, and enabling additional sensors.

## Usage

| Action | How |
|---|---|
| Show / hide the overlay | **Right Ctrl + Right Alt** (default). Change it under *Overlay → Hotkey*. |
| Move the overlay | *Overlay → Unlock to move*, or from the tray menu. Drag it into place, then lock it again. |
| Pin to a corner or monitor | *Overlay → Position*. |
| Change units, thresholds and startup | *Settings*. |
| Quit | Right-click the tray icon → **Exit**. Closing the main window keeps Ember running in the tray. |

The overlay is visible over windowed and borderless-fullscreen games. Exclusive-fullscreen games draw
above all other windows, so the overlay cannot appear there.

### Command-line options

| Option | Description |
|---|---|
| `--snapshot <folder>` | Writes a full sensor report (`sensors.txt`) and screenshots of every page, for bug reports. |
| `--exit` | Closes the running instance. |
| `--toggle-overlay` | Shows or hides the overlay (Linux; used for desktop keyboard shortcuts). |
| `--tray` | Starts in the background without opening the main window. |
| `--profile <file>` | Measures Ember's own CPU usage (Windows). |
| `--register` / `--cleanup` | Creates / removes the startup tasks. Used by the installer (Windows, elevated). |

Settings and the error log are stored in `%APPDATA%\Ember` on Windows and `~/.config/Ember` on Linux.

## Building from source

### Windows installer

Requires the .NET 8 SDK and Inno Setup 6:

```powershell
winget install Microsoft.DotNet.SDK.8 JRSoftware.InnoSetup
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Version <version>
```

The installer is written to `dist\`.

### Linux packages

On Linux with the .NET 8 SDK:

```bash
tools/build-linux.sh --version <version>
```

On Windows, using WSL (Debian):

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-linux.ps1 -Version <version>
```

Output is written to `dist/linux/`: `.deb`, `.rpm`, Arch package, AppImage, tarball, AUR `PKGBUILD` and
`SHA256SUMS`. Package metadata is taken from the `HOMEPAGE`, `MAINTAINER` and `LICENSE` environment
variables.

## Project structure

```
src/Ember/            Windows app (WPF, .NET 8)
  Services/           sensor polling, hotkey, tray, alerts, startup tasks, theming
  ViewModels/         view-models shared with the Linux app
  Views/              main window, pages and the overlay window
  Controls/           ring gauge, bar and sparkline controls
src/Ember.Linux/      Linux app (Avalonia); reads hwmon, /proc, RAPL and NVML
installer/            Inno Setup script
packaging/linux/      desktop entry, AppStream metadata, icons, nfpm config, PKGBUILD template
tools/                build scripts and icon generator
```

## Acknowledgements

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0): sensor access on Windows
- [PawnIO](https://pawnio.eu): signed kernel driver for low-level hardware access on Windows
- [Avalonia](https://avaloniaui.net) (MIT): UI framework for the Linux app

## License

Ember is released under the [MIT License](LICENSE). It is provided as-is, without warranty of any kind.

Ember reads hardware sensors and, on Windows, uses a kernel driver (PawnIO) to do so. Use it at your own risk.
