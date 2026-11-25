#!/bin/sh
# Start a BlueChat media loopback node, run the browser loopback suite against it, stop the node.
#   sh .claude/skills/bluechat-media-acceptance/scripts/loopback.sh [host|linux] [extra env: ONLY=… REPEAT=…]
# host  = node runs directly on Windows (OpenSSL from Git for Windows)   — default
# linux = node runs in mcr.microsoft.com/dotnet/sdk:10.0 (OpenSSL 3.0), ports published by Docker Desktop
# Exit code = the Playwright suite's exit code. Results: tests/phase0/out/loopback-*.json
set -u
# MSYS_NO_PATHCONV only on docker commands: set globally it also stops Git Bash from mapping /dev/null for native
# programs such as curl.exe (curl then fails with a write error although the server answered).
MODE=${1:-host}
ROOT=$(cd "$(dirname "$0")/../../../.." && pwd)
LOG="${TMP:-/tmp}/bc-loopback.log"
cd "$ROOT"
# Docker Desktop needs a Windows path (D:/…); Git Bash's /d/… is not mounted reliably.
DOCKER_ROOT=$(pwd -W 2>/dev/null || pwd)

stop_host() {
  powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process | ? { \$_.CommandLine -match 'MediaLoopback' } | % { Stop-Process -Id \$_.ProcessId -Force }" >/dev/null 2>&1
}

wait_http() {
  # One curl per attempt (curl's own --retry kept failing after the node was up on Windows); up to ~4 minutes for
  # the container's first build. A refused connect already takes ~2 s on Windows; sleep paces other systems.
  i=0
  while [ $i -lt 120 ]; do
    curl -s -o /dev/null --max-time 3 http://127.0.0.1:5299/ && return 0
    sleep 2 2>/dev/null || true
    i=$((i + 1))
  done
  echo "node did not come up; log: $LOG" >&2
  return 1
}

if [ "$MODE" = linux ]; then
  docker rm -f bc-loopback >/dev/null 2>&1
  ADV=$(powershell.exe -NoProfile -Command "(Get-NetIPConfiguration | ? { \$_.IPv4DefaultGateway } | Select -First 1).IPv4Address.IPAddress" | tr -d '\r')
  MSYS_NO_PATHCONV=1 docker run -d --rm --name bc-loopback -p 50000:50000/udp -p 50001:50001/tcp -p 127.0.0.1:5299:5299 \
    -v "$DOCKER_ROOT:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c \
    'mkdir /w && cd /src && tar --exclude="./tests/*/node_modules" --exclude="*/bin" --exclude="*/obj" -cf - . | tar -C /w -xf - && cd /w && dotnet run --project tools/MediaLoopback -- --ip '"$ADV"' --bind 0.0.0.0' >/dev/null
  wait_http || { docker logs bc-loopback > "$LOG" 2>&1; docker stop bc-loopback >/dev/null; exit 1; }
else
  stop_host
  dotnet build tools/MediaLoopback -v q -nologo > "$LOG" 2>&1 || { cat "$LOG"; exit 1; }
  BC_OPENSSL_DIR="C:/Program Files/Git/mingw64/bin" dotnet run --no-build --project tools/MediaLoopback >> "$LOG" 2>&1 &
  wait_http || { stop_host; exit 1; }
fi

(cd tests/phase0 && node loopback-browsers.mjs)
RC=$?
echo "node counters: $(curl -s http://127.0.0.1:5299/node)"

if [ "$MODE" = linux ]; then docker stop bc-loopback >/dev/null; else stop_host; fi
exit $RC
