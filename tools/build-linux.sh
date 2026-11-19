#!/usr/bin/env bash
# Builds Ember's Linux release files into dist/linux/:
#   ember-monitor_<v>-1_amd64.deb             Debian, Ubuntu, Mint, Pop!_OS …
#   ember-monitor-<v>-1.x86_64.rpm            Fedora, openSUSE, RHEL/Alma/Rocky …
#   ember-monitor-<v>-1-x86_64.pkg.tar.zst    Arch, Manjaro, EndeavourOS (pacman -U)
#   PKGBUILD                                  for the AUR (ember-monitor-bin, uses the tarball)
#   Ember-<v>-x86_64.AppImage                 any distro, no install
#   ember-monitor-<v>-linux-x64.tar.gz        plain files (/usr tree)
#   SHA256SUMS
#
# Usage:  tools/build-linux.sh [--version X.Y.Z] [--publish-dir DIR]
#   Without --publish-dir it runs `dotnet publish` (needs the .NET 8 SDK).
#   On Windows, tools/build-linux.ps1 publishes there and runs this script in WSL.
# Optional environment: MAINTAINER, HOMEPAGE (GitHub repo URL), LICENSE (SPDX id).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=""; PUBLISH_DIR=""
while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION=$2; shift 2 ;;
    --publish-dir) PUBLISH_DIR=$(cd "$2" && pwd); shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
done
[ -n "$VERSION" ] || VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/Ember.Linux/Ember.Linux.csproj" | head -n1)
: "${MAINTAINER:=Conner Killian <44344471+Conner-Killian@users.noreply.github.com>}"
: "${HOMEPAGE:=https://github.com/Conner-Killian/ember-monitor}"
: "${LICENSE:=MIT}"
NFPM_VERSION=2.41.0

OUT="$ROOT/dist/linux"
CACHE="$ROOT/tools/.cache"
WORK=$(mktemp -d /tmp/ember-build.XXXXXX)   # Linux filesystem: real permissions, fast
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT" "$CACHE"
echo "==> Ember $VERSION"

need() { command -v "$1" >/dev/null 2>&1; }
# curl (downloads build tools) and file (appimagetool needs it)
if ! need curl || ! need file; then
  if [ "$(id -u)" = 0 ] && need apt-get; then apt-get update -qq && apt-get install -y -qq curl ca-certificates file >/dev/null
  else echo "curl and file are required" >&2; exit 1; fi
fi

# ---- 1. publish ---------------------------------------------------------------------------
if [ -z "$PUBLISH_DIR" ]; then
  need dotnet || { echo "The .NET 8 SDK is needed (or pass --publish-dir)" >&2; exit 1; }
  echo "==> dotnet publish"
  dotnet publish "$ROOT/src/Ember.Linux/Ember.Linux.csproj" -c Release -r linux-x64 --self-contained true \
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version="$VERSION" -o "$WORK/publish" -nologo -v q
  PUBLISH_DIR="$WORK/publish"
fi
[ -f "$PUBLISH_DIR/ember-monitor" ] || { echo "No ember-monitor binary in $PUBLISH_DIR" >&2; exit 1; }

# ---- 2. stage the /usr tree ------------------------------------------------------------------
echo "==> staging"
STAGE="$WORK/stage"
LIB="$STAGE/usr/lib/ember-monitor"
install -d "$LIB" "$STAGE/usr/bin" "$STAGE/usr/share/applications" "$STAGE/usr/share/metainfo" \
           "$STAGE/usr/lib/modules-load.d" "$STAGE/usr/share/doc/ember-monitor"
cp -r "$PUBLISH_DIR"/. "$LIB/"
# .NET's optional LTTng tracing hook needs liblttng-ust, which most systems lack (and Ember doesn't use).
rm -f "$LIB/libcoreclrtraceptprovider.so"
find "$LIB" -type d -exec chmod 755 {} +
find "$LIB" -type f -exec chmod 644 {} +
chmod 755 "$LIB/ember-monitor"
find "$LIB" -name '*.so' -exec chmod 755 {} +
ln -s ../lib/ember-monitor/ember-monitor "$STAGE/usr/bin/ember-monitor"
install -m644 "$ROOT/packaging/linux/ember-monitor.desktop" "$STAGE/usr/share/applications/"
sed -e "s/<release version=\"[^\"]*\" date=\"[^\"]*\"/<release version=\"$VERSION\" date=\"$(date +%F)\"/" \
    "$ROOT/packaging/linux/ember-monitor.metainfo.xml" > "$STAGE/usr/share/metainfo/ember-monitor.metainfo.xml"
install -m644 "$ROOT/packaging/linux/modules-load.conf" "$STAGE/usr/lib/modules-load.d/ember-monitor.conf"
install -m644 "$ROOT/packaging/linux/README-linux.md" "$STAGE/usr/share/doc/ember-monitor/README.md"
install -m644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/ember-monitor/LICENSE"
install -Dm644 "$ROOT/LICENSE" "$STAGE/usr/share/licenses/ember-monitor/LICENSE"   # Arch convention
for d in "$ROOT"/packaging/linux/icons/*/; do
  s=$(basename "$d")
  install -Dm644 "$d/ember-monitor.png" "$STAGE/usr/share/icons/hicolor/$s/apps/ember-monitor.png"
done
install -Dm644 "$ROOT/packaging/linux/icons/ember-monitor.svg" "$STAGE/usr/share/icons/hicolor/scalable/apps/ember-monitor.svg"

# ---- 3. deb / rpm / Arch with nfpm ------------------------------------------------------------
NFPM="$CACHE/nfpm-$NFPM_VERSION"
if [ ! -x "$NFPM" ]; then
  echo "==> downloading nfpm $NFPM_VERSION"
  curl -fsSL "https://github.com/goreleaser/nfpm/releases/download/v${NFPM_VERSION}/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz" | tar -xz -C "$WORK" nfpm
  install -m755 "$WORK/nfpm" "$NFPM"
fi
# nfpm doesn't expand variables in file paths, so write a resolved copy of the config.
esc() { printf '%s' "$1" | sed 's/[&|]/\\&/g'; }
sed -e "s|\${STAGE}|$(esc "$STAGE")|g" -e "s|\${VERSION}|$(esc "$VERSION")|g" -e "s|\${MAINTAINER}|$(esc "$MAINTAINER")|g" \
    -e "s|\${HOMEPAGE}|$(esc "$HOMEPAGE")|g" -e "s|\${LICENSE}|$(esc "$LICENSE")|g" \
    -e "s|packaging/linux/scripts/|$(esc "$ROOT")/packaging/linux/scripts/|g" \
    "$ROOT/packaging/linux/nfpm.yaml" > "$WORK/nfpm.yaml"
rm -f "$OUT"/*.deb "$OUT"/*.rpm "$OUT"/*.pkg.tar.zst
cd "$ROOT"
for p in deb rpm archlinux; do
  echo "==> $p"
  "$NFPM" pkg --config "$WORK/nfpm.yaml" --packager "$p" --target "$OUT/" 2>&1 | grep -v '^using ' || true
  ls "$OUT" | grep -q "$( [ "$p" = archlinux ] && echo pkg.tar.zst || echo "\.$p" )" || { echo "nfpm $p failed" >&2; exit 1; }
done

# ---- 4. tarball + AUR PKGBUILD ----------------------------------------------------------------
echo "==> tarball"
TARDIR="ember-monitor-$VERSION-linux-x64"
mkdir -p "$WORK/tar/$TARDIR"
cp -a "$STAGE/usr" "$WORK/tar/$TARDIR/"
TARBALL="$OUT/$TARDIR.tar.gz"
tar -C "$WORK/tar" --owner=0 --group=0 -czf "$TARBALL" "$TARDIR"
SHA=$(sha256sum "$TARBALL" | cut -d' ' -f1)
sed -e "s|@VERSION@|$VERSION|g" -e "s|@SHA256@|$SHA|g" -e "s|@HOMEPAGE@|$HOMEPAGE|g" \
    -e "s|@MAINTAINER@|$MAINTAINER|g" -e "s|@LICENSE@|$LICENSE|g" \
    "$ROOT/packaging/linux/arch/PKGBUILD.in" > "$OUT/PKGBUILD"

# ---- 5. AppImage ------------------------------------------------------------------------------
echo "==> AppImage"
APPIMAGETOOL="$CACHE/appimagetool-x86_64.AppImage"
if [ ! -x "$APPIMAGETOOL" ]; then
  echo "    downloading appimagetool"
  curl -fsSL -o "$APPIMAGETOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod 755 "$APPIMAGETOOL"
fi
APPDIR="$WORK/AppDir"
mkdir -p "$APPDIR"
cp -a "$STAGE/usr" "$APPDIR/"
rm -rf "$APPDIR/usr/lib/modules-load.d" "$APPDIR/usr/bin"
ln -s usr/lib/ember-monitor/ember-monitor "$APPDIR/AppRun"
cp "$STAGE/usr/share/applications/ember-monitor.desktop" "$APPDIR/"
cp "$ROOT/packaging/linux/icons/256x256/ember-monitor.png" "$APPDIR/ember-monitor.png"
mv "$APPDIR/usr/share/metainfo/ember-monitor.metainfo.xml" "$APPDIR/usr/share/metainfo/ember-monitor.appdata.xml"
rm -f "$OUT"/*.AppImage
# Extract-and-run: works without FUSE (containers, WSL).
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$APPIMAGETOOL" --no-appstream "$APPDIR" "$OUT/Ember-$VERSION-x86_64.AppImage" >/dev/null 2>"$WORK/appimagetool.log" \
  || { cat "$WORK/appimagetool.log" >&2; exit 1; }

# ---- 6. checksums -----------------------------------------------------------------------------
cd "$OUT"
sha256sum -- *.deb *.rpm *.pkg.tar.zst *.AppImage *.tar.gz > SHA256SUMS
echo "==> done:"
ls -lh "$OUT" | tail -n +2
