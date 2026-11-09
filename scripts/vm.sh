#!/bin/zsh
# Builds and runs the Windows app in the Windows VM from the Mac. The source stays on the Mac;
# every command first copies the working tree (tracked and new files, nothing ignored) to the VM.
#
#   scripts/vm.sh sync                  # only copy
#   scripts/vm.sh build                 # copy, build the app (Debug, ARM64)
#   scripts/vm.sh test                  # copy, run the core tests
#   scripts/vm.sh run [--mocks]         # build and start the app on the VM's desktop; --mocks uses the mock miners
#                                       # (start them with --host 0.0.0.0 --advertise 192.168.64.1 for the Stratum probe)
#                                       # (MINERGURU_THEME=light|dark and MINERGURU_OPEN=:48002 pass through,
#                                       # MINERGURU_MOCK_MINERS=n starts with only the first n miners, the scan finds all)
#   scripts/vm.sh shot [file.png] [n]   # screenshot of the app window to the Mac, after n wheel ticks down
#   scripts/vm.sh act file.png 'click 400 300' 'wait 800' ...   # input steps in the app window, then a shot (see shot.ps1)
#                                       # MINERGURU_DESK=1: coordinates and shot of the whole screen (tray, notifications)
#   scripts/vm.sh stop                  # close the app
#
# VM: MINERGURU_VM=user@host, needs OpenSSH with PowerShell as its shell, the Mac's key and the .NET SDK.
# Set it in the environment or in ~/.config/minerguru/env (MINERGURU_CONFIG), which stays outside the repository.
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${MINERGURU_CONFIG:-$HOME/.config/minerguru/env}"
[[ -f "$CONFIG" ]] && source "$CONFIG"
VM="${MINERGURU_VM:?Set MINERGURU_VM to user@host of the Windows VM, e.g. in ~/.config/minerguru/env}"
# The VM user's home folder; the working copy lives in src\MinerGuru_Windows below it.
REMOTE_HOME="${MINERGURU_VM_HOME:-$(ssh -o BatchMode=yes "$VM" '$env:USERPROFILE' | tr -d '\r')}"
REMOTE_DIR="$REMOTE_HOME\\src\\MinerGuru_Windows"
REMOTE_TAR="$REMOTE_HOME\\src\\MinerGuru_Windows.tar"
APP_EXE="$REMOTE_DIR\\src\\MinerGuru\\bin\\ARM64\\Debug\\net10.0-windows10.0.26100.0\\win-arm64\\MinerGuru.exe"
# The Mac as seen from the VM (UTM shared network): the mock miners run there.
MAC_FROM_VM="${MINERGURU_MAC_FROM_VM:-192.168.64.1}"

ps() {
  # PATH of a fresh login, so tools installed after sshd started are found.
  ssh -o BatchMode=yes "$VM" "\$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User'); \$ProgressPreference = 'SilentlyContinue'; $1"
}

sync() {
  local tar=$(mktemp -t mgwin).tar
  # Tracked and new files that exist; a file deleted but not yet committed is left out.
  local files=(${(0)"$(git ls-files -co --exclude-standard -z)"})
  files=(${^files}(N))
  COPYFILE_DISABLE=1 tar -cf "$tar" "${files[@]}"
  scp -q "$tar" "${VM}:src/MinerGuru_Windows.tar"
  rm -f "$tar"
  # Replace the sources but keep bin/obj, so builds stay incremental.
  ps "New-Item -ItemType Directory -Force '$REMOTE_DIR' | Out-Null; Get-ChildItem '$REMOTE_DIR' -Recurse -File | Where-Object { \$_.FullName -notmatch '\\\\(bin|obj)\\\\' } | Remove-Item -Force; tar -xf '$REMOTE_TAR' -C '$REMOTE_DIR'; Remove-Item '$REMOTE_TAR'"
}

build() {
  # A running app locks its exe.
  ps "Get-Process MinerGuru -ErrorAction SilentlyContinue | Stop-Process -Force; cd '$REMOTE_DIR'; dotnet build src\\MinerGuru\\MinerGuru.csproj -p:Platform=ARM64 -nologo -v q -clp:NoSummary 2>&1 | Where-Object { \$_ -match 'error|warn' }; if (\$LASTEXITCODE) { exit 1 }"
}

# Starts a program in the user's desktop session: processes started over SSH would run invisibly in session 0.
interactive() {
  local name=$1 command=$2
  ps "schtasks /create /tn '$name' /tr '$command' /sc once /st 00:00 /it /f | Out-Null; schtasks /run /tn '$name' | Out-Null; Start-Sleep -Seconds 1; schtasks /delete /tn '$name' /f | Out-Null"
}

case "${1:-build}" in
  sync) sync ;;
  build) sync; build ;;
  test)
    sync
    ps "cd '$REMOTE_DIR'; dotnet test tests\\MinerGuru.Core.Tests -nologo -v q 2>&1 | Select-Object -Last 15"
    ;;
  run)
    sync; build
    ps "Get-Process MinerGuru -ErrorAction SilentlyContinue | Stop-Process -Force; exit 0"
    if [[ "${2:-}" == "--mocks" ]]; then
      # Own data folder with the running mock miners, like the Mac's mock build. Stable ids keep their power state.
      python3 - "$MAC_FROM_VM" "${MINERGURU_MOCK_MINERS:-99}" > /tmp/mg-mock-settings.json <<'EOF'
import json, sys, uuid
from pathlib import Path
host = sys.argv[1]
# The Mac repository next to this one holds the mock miners.
variants = sorted(Path("../MinerGuru/MockMiners/variants").glob("*.json"))
print(json.dumps({"Miners": [{"Id": str(uuid.uuid5(uuid.NAMESPACE_URL, p.stem)), "IpAddress": f"{host}:{48001 + i}", "CustomName": p.stem, "AddedAt": "2026-01-01T00:00:00Z"}
                             for i, p in enumerate(variants) if i < int(sys.argv[2])], "PollingIntervalSeconds": 5, "TempWarningThresholdC": 68}, indent=1))
EOF
      ps "New-Item -ItemType Directory -Force '$REMOTE_HOME\\MinerGuruMock' | Out-Null"
      scp -q /tmp/mg-mock-settings.json "${VM}:MinerGuruMock/settings.json"
      # MINERGURU_THEME and MINERGURU_OPEN on the Mac are handed to the app (see MainWindow).
      printf '@set MINERGURU_DATA_DIR=%s\r\n@set MINERGURU_SCAN_EXTRA=%s:48001-48024\r\n@set MINERGURU_UPDATE_URL=%s\r\n@set MINERGURU_THEME=%s\r\n@set MINERGURU_OPEN=%s\r\n@start "" "%s"\r\n' \
        "$REMOTE_HOME\\MinerGuruMock" "$MAC_FROM_VM" "${MINERGURU_UPDATE_URL:-}" "${MINERGURU_THEME:-}" "${MINERGURU_OPEN:-}" "$APP_EXE" > /tmp/mg-mock-run.cmd
      scp -q /tmp/mg-mock-run.cmd "${VM}:MinerGuruMock/run.cmd"
      interactive MinerGuruRun "$REMOTE_HOME\\MinerGuruMock\\run.cmd"
    else
      interactive MinerGuruRun "$APP_EXE"
    fi
    ;;
  shot)
    out="${2:-/tmp/minerguru-vm.png}"
    sync
    ps "Remove-Item '$REMOTE_HOME\\src\\shot.png' -ErrorAction SilentlyContinue; exit 0"
    interactive MinerGuruShot "conhost --headless powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $REMOTE_DIR\\scripts\\vm\\shot.ps1 -Scroll ${3:-0}"
    sleep $(( 4 + ${3:-0} / 15 ))
    scp -q "${VM}:src/shot.png" "$out"
    echo "$out"
    ;;
  act)
    out="$2"; shift 2
    printf '%s\r\n' "$@" > /tmp/mg-steps.txt
    scp -q /tmp/mg-steps.txt "${VM}:src/steps.txt"
    ps "Remove-Item '$REMOTE_HOME\\src\\shot.png' -ErrorAction SilentlyContinue; exit 0"
    interactive MinerGuruShot "conhost --headless powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $REMOTE_DIR\\scripts\\vm\\shot.ps1 -Steps $REMOTE_HOME\\src\\steps.txt ${MINERGURU_DESK:+-Desktop}"
    for i in {1..30}; do sleep 1; scp -q "${VM}:src/shot.png" "$out" 2>/dev/null && break; done
    echo "$out"
    ;;
  stop) ps "Get-Process MinerGuru -ErrorAction SilentlyContinue | Stop-Process -Force; exit 0" ;;
  *) echo "unknown command: $1" >&2; exit 2 ;;
esac
