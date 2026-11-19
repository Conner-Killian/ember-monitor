# Ember

A good-looking PC temperature monitor for **Windows 10/11 and Linux**: a small always-on-top **overlay**
for your desktop and games, plus a full **dashboard** with live graphs.

![icon](src/Ember/Assets/ember.png)

**Linux:** .deb (Debian/Ubuntu), .rpm (Fedora/openSUSE), Arch package and AppImage. See
[packaging/linux/README-linux.md](packaging/linux/README-linux.md) for install commands, the Wayland
hotkey and sensor setup.

## Installing on Windows (for friends)

1. Run `Ember-Setup-1.0.6.exe`.
2. Windows SmartScreen may say *"Windows protected your PC"* because the installer isn't code-signed.
   Click **More info → Run anyway**.
3. Leave **"Install the PawnIO driver"** ticked. It downloads the official signed driver from
   [pawnio.eu](https://pawnio.eu), and Ember needs it to read CPU, RAM and motherboard temperatures.
   GPU and drive temperatures work without it.
4. Ember needs administrator rights, because Windows only exposes CPU/RAM sensors to elevated apps.
   You only approve that once, during install: the installer registers two scheduled tasks, so after that Ember
   starts without a UAC prompt, both at sign-in and when you open it from the Start menu, a shortcut or Raycast.
   Opening it while it's already running just brings the dashboard to the front.

## Using it

| | |
|---|---|
| **Toggle overlay** | **Right Ctrl + Right Alt** by default. Press and release both with no other key. You can change it under *Overlay → Hotkey*. |
| **Move overlay** | *Overlay → Unlock to move* (or tray menu). Drag it anywhere on screen, even onto the taskbar. Lock it again and clicks pass straight through. |
| **Overlay styles** | Pill, Card or Rings. Show CPU / GPU / RAM / each drive, usage %, power draw; opacity and size sliders. |
| **Units** | °C / °F in *Settings* or the tray menu. |
| **Alerts** | Readings turn amber or red at thresholds you set. Optional Windows notification when something stays hot. |
| **Tray** | Closing the window keeps Ember in the tray. Right-click the flame icon for quick options or **Exit**. |

The overlay works over borderless/windowed games. Exclusive-fullscreen games draw over everything, so the overlay can't show there.

## Building from source

Requirements: .NET 8 SDK and Inno Setup 6 (`winget install Microsoft.DotNet.SDK.8 JRSoftware.InnoSetup`).

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Version 1.0.6
```

The installer lands in `dist\`. The app is published self-contained, so friends don't need .NET installed.

**Linux packages** (from Windows, needs WSL with Debian: `wsl --install -d Debian`):

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-linux.ps1
```

or on a Linux machine with the .NET 8 SDK: `tools/build-linux.sh`. Everything lands in `dist/linux/`.
Set `HOMEPAGE` (your GitHub repo URL), `MAINTAINER` and `LICENSE` before a public release; they go into
the package metadata and the AUR `PKGBUILD`.

### Project layout

- `src/Ember.Linux`: the Linux app (Avalonia). Shares the settings model, sensor model, temperature
  formatting, alerts and view-models with the Windows app as linked files; Linux sensors come from
  hwmon (`/sys/class/hwmon`), `/proc`, RAPL and NVML
- `packaging/linux`: desktop entry, AppStream metadata, icons, nfpm config (deb/rpm/Arch), AUR PKGBUILD template

- `src/Ember/Services`: sensor polling (LibreHardwareMonitor), global hotkey (low-level keyboard hook), startup task, alerts, tray, theming
- `src/Ember/Views`: main window + pages (Dashboard, All sensors, Overlay, Settings) and the overlay window
- `src/Ember/Controls`: custom-drawn ring gauge, bar and sparkline
- `installer/Ember.iss`: Inno Setup script
- `tools/make-icon.ps1`: regenerates the app icon

### Diagnostics

- `Ember.exe --snapshot <folder>`: renders every page and overlay style to PNG and writes `sensors.txt`, a full sensor dump
- `Ember.exe --profile <file>`: measures CPU use with the window open/hidden and the overlay on/off
- `Ember.exe --exit`: closes a running instance (the installer uses this)
- `Ember.exe --register` / `--cleanup`: create / remove the no-UAC scheduled tasks (run elevated, by the installer and uninstaller)
- Settings and `error.log` live in `%APPDATA%\Ember`

Sensor data is provided by [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0).
