# Ember for Linux

Ember shows CPU, GPU, RAM and drive temperatures in a small always-on-top **overlay** and a full
**dashboard** with live graphs. It runs on GNOME, KDE Plasma, Xfce, Cinnamon, MATE and others,
in both Wayland and X11 sessions. No root access is needed to run it.

## Install

| Distribution | File | Command |
|---|---|---|
| Debian 12/13, Ubuntu 22.04+, Mint, Pop!_OS | `ember-monitor_<version>-1_amd64.deb` | `sudo apt install ./ember-monitor_*_amd64.deb` |
| Fedora, openSUSE, RHEL/Alma/Rocky 9+ | `ember-monitor-<version>-1.x86_64.rpm` | `sudo dnf install ./ember-monitor-*.rpm` (openSUSE: `sudo zypper install ./ember-monitor-*.rpm`) |
| Arch, Manjaro, EndeavourOS | `ember-monitor-<version>-1-x86_64.pkg.tar.zst` | `sudo pacman -U ./ember-monitor-*.pkg.tar.zst` (or the `ember-monitor-bin` AUR package) |
| Anything else | `Ember-<version>-x86_64.AppImage` | `chmod +x Ember-*.AppImage && ./Ember-*.AppImage` |

Then open **Ember** from your app menu. It starts on login by default (Settings → Start on login).

To uninstall: `sudo apt remove ember-monitor`, `sudo dnf remove ember-monitor` or `sudo pacman -R ember-monitor`.
Your settings stay in `~/.config/Ember` until you delete that folder.

## Hotkeys

The overlay's show / hide hotkey (**Right Ctrl + Right Alt** by default) and each overlay preset's
hotkey work as on Windows: set them on the Overlay page, with left and right modifiers told apart and
modifier-only combinations allowed.

| Session | How it works |
|---|---|
| X11 (Xfce, Cinnamon, MATE, "GNOME on Xorg"…) | Works straight away. |
| Wayland (GNOME, KDE Plasma, Sway, Hyprland…) | Wayland doesn't let apps see key presses by default. The Overlay page shows **Allow hotkeys** once: it asks for your password and adds a udev rule (`/etc/udev/rules.d/70-ember-monitor-hotkeys.rules`) that lets the user at the screen read the keyboard. Programs you run can then read the keyboard, as they always could on X11. Uninstalling the .deb/.rpm/Arch package removes the rule; with the AppImage, delete that file yourself. |

If allowing isn't possible (no `pkexec`), the Overlay page shows the equivalent terminal command.
Without keyboard access at all, you can still bind `ember-monitor --toggle-overlay` or
`ember-monitor --preset NAME` to a shortcut in your desktop's keyboard settings.

## Tray icon

Ember adds a tray (status) icon on KDE, Xfce, Cinnamon and MATE. Stock GNOME has no tray; install the
**AppIndicator and KStatusNotifierItem Support** extension (`gnome-shell-extension-appindicator` on Debian,
preinstalled on Ubuntu) to get it. Without it, closing the window keeps Ember running in the
background, and opening Ember from the app menu brings the window back.

## Sensors

Most readings work out of the box. **Settings → Sensor setup** lists anything missing on your system,
with the command that fixes it:

- **NVIDIA cards** need the proprietary NVIDIA driver (it provides `libnvidia-ml`). The open-source
  nouveau driver reports only a temperature.
- **SATA drives** report temperatures through the `drivetemp` module. The .deb/.rpm/Arch packages load
  it automatically; with the AppImage run `sudo modprobe drivetemp`.
- **CPU power draw** comes from the RAPL energy counter, which the kernel lets only root read.
  `sudo chmod o+r /sys/class/powercap/intel-rapl:0/energy_uj` allows it until the next reboot.
- **RAM temperatures** need DDR5 modules and the `spd5118` driver (Linux 6.11+).
- **Motherboard fans and temperatures** need the driver for your board's sensor chip; `sudo sensors-detect`
  (lm-sensors) finds it. The NCT6687D on recent MSI boards needs the out-of-tree `nct6687d` driver.

## Command line

```
ember-monitor                   open Ember (or bring the running window forward)
ember-monitor --tray            start in the background
ember-monitor --toggle-overlay  show / hide the overlay
ember-monitor --preset NAME     switch the overlay to a preset (again while it's showing hides it)
ember-monitor --exit            quit the running Ember
ember-monitor --snapshot DIR    write a sensor report and screenshots to DIR (for bug reports)
```
