#!/usr/bin/env bash
# Installs the VU Studio agent on a Steam Deck (or any Linux box) without root.
#
#   * program:  ~/.local/share/vustudio-agent/            (self-contained, no .NET install needed)
#   * settings: ~/.config/vustudio-agent/agent.env         (port, token, name)
#   * service:  ~/.config/systemd/user/vustudio-agent.service
#
# Everything lives under $HOME, which SteamOS keeps across system updates (/usr is replaced).
#
# Usage:
#   install.sh                     build from this repository (needs the .NET 10 SDK) or use ./bundle
#   install.sh --from DIR|FILE.tar.gz   use a prebuilt self-contained publish (a folder or a tarball)
#   install.sh --port 5342 --name "Steam Deck" --token SECRET
#   install.sh --uninstall

set -euo pipefail

PORT=5342
NAME=""
TOKEN=""
FROM=""
RID="linux-x64"
UNINSTALL=0

while [ $# -gt 0 ]; do
  case "$1" in
    --port) PORT="$2"; shift 2 ;;
    --name) NAME="$2"; shift 2 ;;
    --token) TOKEN="$2"; shift 2 ;;
    --from) FROM="$2"; shift 2 ;;
    --rid) RID="$2"; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [ "$(id -u)" -eq 0 ]; then
  echo "Run this as your normal user (on the Deck: 'deck'), not as root." >&2
  exit 1
fi

INSTALL_DIR="$HOME/.local/share/vustudio-agent"
CONFIG_DIR="$HOME/.config/vustudio-agent"
UNIT_DIR="$HOME/.config/systemd/user"
UNIT="$UNIT_DIR/vustudio-agent.service"
ENV_FILE="$CONFIG_DIR/agent.env"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ "$UNINSTALL" -eq 1 ]; then
  systemctl --user disable --now vustudio-agent.service 2>/dev/null || true
  rm -f "$UNIT"
  systemctl --user daemon-reload 2>/dev/null || true
  rm -rf "$INSTALL_DIR"
  echo "Removed the program and the service. Settings kept in $CONFIG_DIR (delete it by hand if you like)."
  exit 0
fi

# Stage the files in a temporary folder next to the final one so a failed build never breaks a working install.
STAGE="$(mktemp -d "$HOME/.local/share/.vustudio-agent-stage.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT

if [ -n "$FROM" ]; then
  if [ -d "$FROM" ]; then
    cp -a "$FROM"/. "$STAGE"/
  else
    tar -xzf "$FROM" -C "$STAGE"
  fi
elif [ -x "$SCRIPT_DIR/bundle/vustudio-agent" ]; then
  cp -a "$SCRIPT_DIR/bundle"/. "$STAGE"/
else
  REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
  if [ ! -f "$REPO_ROOT/src/VuStudio.Agent/VuStudio.Agent.csproj" ]; then
    echo "No prebuilt agent found. Pass --from DIR|FILE.tar.gz, or run this script from a repository checkout." >&2
    exit 1
  fi
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "Building from source needs the .NET 10 SDK. Build the agent on another machine with:" >&2
    echo "  dotnet publish src/VuStudio.Agent -c Release -r $RID --self-contained -p:PublishSingleFile=true -o publish" >&2
    echo "and copy the folder here, then run: install.sh --from publish" >&2
    exit 1
  fi
  echo "Publishing a self-contained build ($RID)..."
  dotnet publish "$REPO_ROOT/src/VuStudio.Agent/VuStudio.Agent.csproj" -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "$STAGE"
fi

if [ ! -f "$STAGE/vustudio-agent" ]; then
  echo "The package does not contain a 'vustudio-agent' executable." >&2
  exit 1
fi
chmod +x "$STAGE/vustudio-agent"

systemctl --user stop vustudio-agent.service 2>/dev/null || true
rm -rf "$INSTALL_DIR"
mkdir -p "$(dirname "$INSTALL_DIR")"
mv "$STAGE" "$INSTALL_DIR"
trap - EXIT

# Settings: keep an existing file; create one with a random token on the first install.
mkdir -p "$CONFIG_DIR"
if [ ! -f "$ENV_FILE" ]; then
  if [ -z "$TOKEN" ]; then
    TOKEN="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
  fi
  {
    echo "VUSTUDIO_AGENT_PORT=$PORT"
    echo "VUSTUDIO_AGENT_TOKEN=$TOKEN"
    if [ -n "$NAME" ]; then echo "VUSTUDIO_AGENT_NAME=$NAME"; else echo "VUSTUDIO_AGENT_NAME=$(hostname)"; fi
  } > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  NEW_TOKEN=1
else
  NEW_TOKEN=0
fi

mkdir -p "$UNIT_DIR"
cat > "$UNIT" <<EOF
[Unit]
Description=VU Studio agent

[Service]
Type=simple
ExecStart=$INSTALL_DIR/vustudio-agent
EnvironmentFile=$ENV_FILE
Restart=always
RestartSec=5
# The Deck suspends: the agent simply comes back after resume.

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable --now vustudio-agent.service

# Keep the user manager (and the agent) alive when no session is open, e.g. after switching mode.
if command -v loginctl >/dev/null 2>&1; then
  loginctl enable-linger "$USER" 2>/dev/null || echo "Note: could not enable lingering; the agent runs while you are logged in (Game Mode counts)."
fi

echo
echo "VU Studio agent installed and running on port $(grep -m1 '^VUSTUDIO_AGENT_PORT=' "$ENV_FILE" | cut -d= -f2)."
if [ "$NEW_TOKEN" -eq 1 ]; then
  echo "Access token (enter it in VU Studio): $TOKEN"
else
  echo "Existing settings kept: $ENV_FILE"
fi
echo "Check it:  curl -H \"Authorization: Bearer <token>\" http://localhost:$(grep -m1 '^VUSTUDIO_AGENT_PORT=' "$ENV_FILE" | cut -d= -f2)/api/v1/info"
echo "Logs:      journalctl --user -u vustudio-agent -f"
