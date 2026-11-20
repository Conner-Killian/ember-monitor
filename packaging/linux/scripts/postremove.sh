#!/bin/sh
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true

# Uninstalled (not upgraded): take back the keyboard access for hotkeys that Ember added when the
# user pressed "Allow hotkeys" (Overlay page). deb passes remove/purge, rpm 0, Arch the old version;
# upgrades (deb "upgrade", rpm 1) keep it.
case "$1" in
  upgrade|failed-upgrade|abort-install|abort-upgrade|disappear|1|2) ;;
  *)
    if [ -f /etc/udev/rules.d/70-ember-monitor-hotkeys.rules ]; then
      rm -f /etc/udev/rules.d/70-ember-monitor-hotkeys.rules
      command -v udevadm >/dev/null 2>&1 && { udevadm control --reload-rules; udevadm trigger --subsystem-match=input --action=change; } || true
    fi
    ;;
esac
exit 0
