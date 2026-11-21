#!/usr/bin/env bash
# Builds dist/lightweight-ide_<version>_amd64.deb: a self-contained publish (no .NET runtime package needed;
# the .NET SDK for building projects is the one on PATH, e.g. ~/.dotnet) installed to /opt/lightweight-ide.
# usage: packaging/build-deb.sh [version]     (default: the version from the latest v* tag, else 0.1.0)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-$(git -C "$root" describe --tags --abbrev=0 --match 'v*' 2>/dev/null | sed 's/^v//' || true)}"
version="${version:-0.1.0}"
arch=amd64
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
chmod 755 "$stage" # mktemp creates it 0700; it becomes the root of the package

echo "Publishing $version (self-contained linux-x64)…"
dotnet publish "$root/src/LightweightIDE.App" -c Release -r linux-x64 --self-contained true \
  -p:Version="$version" -p:InformationalVersion="$version" -o "$stage/opt/lightweight-ide" --nologo -v q

mkdir -p "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/scalable/apps" "$stage/DEBIAN"
ln -s /opt/lightweight-ide/lightweight-ide "$stage/usr/bin/lightweight-ide"
install -m 644 "$root/packaging/lightweight-ide.desktop" "$stage/usr/share/applications/"
install -m 644 "$root/packaging/lightweight-ide.svg" "$stage/usr/share/icons/hicolor/scalable/apps/"
# Documentation required by Debian policy: copyright and a changelog.
doc="$stage/usr/share/doc/lightweight-ide"
mkdir -p "$doc"
cat > "$doc/copyright" <<COPYRIGHT
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: lightweight-ide
Source: https://github.com/ambit-project/ambit-editor-dotnet

Files: *
Copyright: 2026 Krzysztof Łopuch
License: MIT
 The bundled .NET runtime, Roslyn, GirCore, Markdig and SQLite components keep
 their own (MIT / Apache-2.0 / public domain) licenses; see the files under
 /opt/lightweight-ide.

License: MIT
$(sed 's/^$/./; s/^/ /' "$root/LICENSE")
COPYRIGHT
printf 'lightweight-ide (%s) unstable; urgency=low\n\n  * Release %s.\n\n -- Ambit <dev@ambit.invalid>  %s\n' \
  "$version" "$version" "$(date -R)" | gzip -9n > "$doc/changelog.gz"

# Standard permissions everywhere: directories 755, files 644, only the launcher and createdump executable.
umask 022
find "$stage" -path "$stage/DEBIAN" -prune -o -type d -exec chmod 755 {} +
find "$stage" -path "$stage/DEBIAN" -prune -o -type f -exec chmod 644 {} +
chmod 755 "$stage/opt/lightweight-ide/lightweight-ide"
[[ -f "$stage/opt/lightweight-ide/createdump" ]] && chmod 755 "$stage/opt/lightweight-ide/createdump"

size_kb=$(du -sk --exclude=DEBIAN "$stage" | cut -f1)
cat > "$stage/DEBIAN/control" <<CONTROL
Package: lightweight-ide
Version: $version
Section: devel
Priority: optional
Architecture: $arch
Installed-Size: $size_kb
Depends: libgtk-4-1 (>= 4.14), libgtksourceview-5-0 (>= 5.10), libc6, libicu74 | libicu72 | libicu70
Recommends: libvte-2.91-gtk4-0, git
Maintainer: Ambit <dev@ambit.invalid>
Homepage: https://github.com/ambit-project/ambit-editor-dotnet
Description: Lightweight native C# / .NET IDE (GTK4)
 Editor, Roslyn code intelligence, solution explorer, build, tests, NuGet,
 debugger (netcoredbg), terminal and Markdown preview for web and backend
 dotnet solutions. Needs a .NET SDK (10 or newer) on PATH to build projects.
 For debugging, put netcoredbg in ~/.local/share/lightweight-ide/netcoredbg
 or on PATH.
CONTROL

mkdir -p "$root/dist"
out="$root/dist/lightweight-ide_${version}_${arch}.deb"
dpkg-deb --build --root-owner-group -Zxz "$stage" "$out" >/dev/null
echo "$out ($(du -h "$out" | cut -f1))"
# Checksum next to the package, with the bare file name so `sha256sum -c` works in the download folder.
(cd "$root/dist" && sha256sum "$(basename "$out")" > "$(basename "$out").sha256")
