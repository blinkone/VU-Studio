#!/usr/bin/env bash
# Publishes VU Studio into a staging folder ready for installer/VuStudio.iss.
#
#   build/publish.sh [-v VERSION] [-o OUTDIR] [--skip-web] [--skip-agent]
#
# Output layout (OUTDIR defaults to <repo>/artifacts):
#   OUTDIR/stage/          Server (win-x64) + Tray (win-x64), the Inno Setup /DStageDir
#   OUTDIR/agent/<rid>/    Agent single-file binaries (linux-x64, linux-arm64, win-x64)
#   OUTDIR/obj/            intermediate per-project publish folders
set -euo pipefail

VERSION="0.0.0-dev"
OUT=""
SKIP_WEB=0
SKIP_AGENT=0

usage() { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    -v|--version)  VERSION="${2:?missing value for $1}"; shift 2 ;;
    -o|--output)   OUT="${2:?missing value for $1}"; shift 2 ;;
    --skip-web)    SKIP_WEB=1; shift ;;
    --skip-agent)  SKIP_AGENT=1; shift ;;
    -h|--help)     usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ -n "$OUT" ] || OUT="$ROOT/artifacts"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

# Numeric file version: strip "+build" then "-prerelease".
CORE="${VERSION%%+*}"; CORE="${CORE%%-*}"

export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
COMMON=(-c Release "-p:Version=$VERSION" "-p:ContinuousIntegrationBuild=true" -nologo -v minimal)

STAGE="$OUT/stage"
OBJ="$OUT/obj"
rm -rf "$STAGE" "$OUT/agent" "$OBJ"
mkdir -p "$STAGE" "$OBJ"

echo "== VU Studio $VERSION (file version $CORE) -> $OUT"

if [ "$SKIP_WEB" -eq 0 ]; then
  echo "== Web UI"
  (cd "$ROOT/web" && npm ci && npm run build)
else
  echo "== Web UI skipped"
fi
if [ ! -f "$ROOT/src/VuStudio.Server/wwwroot/index.html" ]; then
  echo "ERROR: src/VuStudio.Server/wwwroot/index.html is missing (web UI not built)." >&2
  exit 1
fi

echo "== Server (win-x64)"
dotnet publish "$ROOT/src/VuStudio.Server/VuStudio.Server.csproj" "${COMMON[@]}" \
  -r win-x64 --self-contained true -p:IsTransformWebConfigDisabled=true \
  -o "$OBJ/server"

echo "== Tray (win-x64)"
dotnet publish "$ROOT/src/VuStudio.Tray/VuStudio.Tray.csproj" "${COMMON[@]}" \
  -r win-x64 --self-contained true \
  -o "$OBJ/tray"

# Server first, Tray second: same-named files differ (System.Drawing.dll and others)
# and the Tray's copies must win.
echo "== Staging -> $STAGE"
cp -a "$OBJ/server/." "$STAGE/"
cp -a "$OBJ/tray/." "$STAGE/"

if [ -d "$ROOT/src/VuStudio.Agent" ] && [ "$SKIP_AGENT" -eq 0 ]; then
  for rid in linux-x64 linux-arm64 win-x64; do
    echo "== Agent ($rid)"
    dotnet publish "$ROOT/src/VuStudio.Agent/VuStudio.Agent.csproj" "${COMMON[@]}" \
      -r "$rid" --self-contained true -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
      -o "$OUT/agent/$rid"
  done
fi

echo "== Done"
echo "Stage: $STAGE"
echo "Installer: ISCC.exe /DAppVersion=$VERSION /DStageDir=<stage> installer\\VuStudio.iss"
