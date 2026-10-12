#!/bin/bash
# SessionStart hook for Claude Code cloud sessions: makes builds, tests and the web typecheck work.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-$(pwd)}"

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1' >> "$CLAUDE_ENV_FILE"
  echo 'export DOTNET_NOLOGO=1' >> "$CLAUDE_ENV_FILE"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# .NET 10 SDK is not preinstalled. builds.dotnet.microsoft.com is blocked by the egress proxy,
# but Ubuntu's own feed ships dotnet-sdk-10.0.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  SUDO=""
  if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi
  export DEBIAN_FRONTEND=noninteractive
  $SUDO apt-get update -qq
  $SUDO apt-get install -y -qq dotnet-sdk-10.0 >/dev/null
fi

dotnet restore VuStudio.slnx --verbosity quiet

if [ -f web/package.json ]; then
  (cd web && npm install --no-audit --no-fund --loglevel=error)
fi
