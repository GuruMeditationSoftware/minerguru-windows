#!/bin/zsh
# Builds the Windows installers in the VM (see vm.sh; needs the .NET SDK and Inno Setup 6 there).
# Usage: scripts/release.sh   → dist/MinerGuru-<version>-{x64,arm64}.exe + dist/upload/windows/{MinerGuru-x64.exe,MinerGuru-arm64.exe,SHA256SUMS.txt}
#
# The installers are not signed, so Windows SmartScreen warns on first start. The upload names carry no version.
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${MINERGURU_CONFIG:-$HOME/.config/minerguru/env}"
[[ -f "$CONFIG" ]] && source "$CONFIG"
VM="${MINERGURU_VM:?Set MINERGURU_VM to user@host of the Windows VM, e.g. in ~/.config/minerguru/env}"
# Personal traces the installers must not contain. The patterns are personal themselves, so they live outside the
# repository (MINERGURU_TRACES, default ~/.config/minerguru/traces) and only visit the VM for the check.
TRACES_FILE="${MINERGURU_TRACES:-$HOME/.config/minerguru/traces}"
grep -q -v -e '^#' -e '^[[:space:]]*$' "$TRACES_FILE" 2>/dev/null || { echo "❌ No trace patterns in $TRACES_FILE, no release."; exit 1; }
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' src/MinerGuru/MinerGuru.csproj)
UPLOAD_DIR="dist/upload/windows"

scripts/vm.sh sync
scp -q "$TRACES_FILE" "${VM}:src/minerguru-traces.txt"
trap 'ssh -o BatchMode=yes "$VM" "Remove-Item src\\minerguru-traces.txt -ErrorAction SilentlyContinue" 2>/dev/null || true' EXIT
ssh -o BatchMode=yes "$VM" "powershell -NoProfile -ExecutionPolicy Bypass -File src\\MinerGuru_Windows\\scripts\\vm\\release.ps1 -Version $VERSION -TracesFile src\\minerguru-traces.txt"

rm -rf "$UPLOAD_DIR"
mkdir -p "$UPLOAD_DIR"
for arch in x64 arm64; do
  scp -q "${VM}:src/MinerGuru_Windows/src/MinerGuru/bin/release/MinerGuru-$VERSION-$arch.exe" dist/
  cp "dist/MinerGuru-$VERSION-$arch.exe" "$UPLOAD_DIR/MinerGuru-$arch.exe"
done
(cd "$UPLOAD_DIR" && shasum -a 256 MinerGuru-*.exe > SHA256SUMS.txt)

echo "Installers for v$VERSION:"
ls -lh "$UPLOAD_DIR"
cat "$UPLOAD_DIR/SHA256SUMS.txt"
