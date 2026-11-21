#!/usr/bin/env bash
# Installs Lightweight IDE for the current user:
#   ~/.local/share/lightweight-ide/app/   published app (netcoredbg/ next to it is the debugger) (framework-dependent, needs the .NET runtime/SDK)
#   ~/.local/bin/lightweight-ide      launcher symlink
#   ~/.local/share/applications/      desktop entry
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
prefix="${XDG_DATA_HOME:-$HOME/.local/share}"
target="$prefix/lightweight-ide/app"

# Read the library cache once: `ldconfig -p | grep -q` fails under pipefail (SIGPIPE when grep exits early).
libraries="$(ldconfig -p)"
missing=()
for lib in libgtk-4.so.1 libgtksourceview-5.so.0; do
  [[ "$libraries" == *"$lib"* ]] || missing+=("$lib")
done
if ((${#missing[@]})); then
  echo "Missing native libraries: ${missing[*]}"
  echo "Install them with: sudo apt install libgtk-4-1 libgtksourceview-5-0 libvte-2.91-gtk4-0"
  exit 1
fi
[[ "$libraries" == *libvte-2.91-gtk4.so.0* ]] || echo "Note: libvte-2.91-gtk4-0 is not installed, the integrated terminal will be disabled."

dotnet publish "$root/src/LightweightIDE.App" -c Release -o "$target" --nologo -v q
mkdir -p "$HOME/.local/bin" "$prefix/applications"
ln -sf "$target/lightweight-ide" "$HOME/.local/bin/lightweight-ide"
cp "$root/packaging/lightweight-ide.desktop" "$prefix/applications/"
mkdir -p "$prefix/icons/hicolor/scalable/apps"
cp "$root/packaging/lightweight-ide.svg" "$prefix/icons/hicolor/scalable/apps/"
update-desktop-database "$prefix/applications" 2>/dev/null || true
echo "Installed to $target. Run: lightweight-ide [folder|solution|file]"
