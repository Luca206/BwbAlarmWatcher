#!/usr/bin/env bash
# Fetches the latest BwbAlarmWatcher2 release from GitHub and installs it.
#
#   sudo /opt/BwbAlarmWatcher2/update.sh          # update (or first install)
#   sudo /opt/BwbAlarmWatcher2/update.sh --force  # reinstall even if up to date
#
# Local configuration survives updates: appsettings.json and bwbAlarmWatcher2.env
# are only created when missing, never overwritten.
#
# For a private repository, store a fine-grained PAT (contents: read) at
# /opt/BwbAlarmWatcher2/.github_token (mode 600). Requires: curl, jq.
set -euo pipefail

INSTALL_DIR="/opt/BwbAlarmWatcher2"
REPO="Luca206/BwbAlarmWatcher2"
ASSET="bwbAlarmWatcher2-linux-arm64.tar.gz"
SERVICE="bwbAlarmWatcher2.service"
SERVICE_USER="bwbalarmwatcher"
TOKEN_FILE="$INSTALL_DIR/.github_token"

[[ $EUID -eq 0 ]] || { echo "ERROR: run as root (sudo $0)" >&2; exit 1; }
command -v jq >/dev/null || { echo "ERROR: jq is required (sudo apt install jq)" >&2; exit 1; }

auth=()
if [[ -f "$TOKEN_FILE" ]]; then
  auth=(-H "Authorization: Bearer $(<"$TOKEN_FILE")")
fi

echo "Checking latest release of $REPO ..."
release_json=$(curl -fsSL "${auth[@]}" -H "Accept: application/vnd.github+json" \
  "https://api.github.com/repos/$REPO/releases/latest")
tag=$(jq -r '.tag_name' <<<"$release_json")
asset_id=$(jq -r --arg name "$ASSET" '.assets[] | select(.name == $name) | .id' <<<"$release_json")

if [[ -z "$tag" || "$tag" == "null" || -z "$asset_id" ]]; then
  echo "ERROR: could not resolve the latest release or asset '$ASSET'" >&2
  exit 1
fi

installed="none"
[[ -f "$INSTALL_DIR/.installed_version" ]] && installed=$(<"$INSTALL_DIR/.installed_version")
if [[ "$installed" == "$tag" && "${1:-}" != "--force" ]]; then
  echo "Already up to date ($tag)."
  exit 0
fi

echo "Installing $tag (was: $installed) ..."
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "${auth[@]}" -H "Accept: application/octet-stream" \
  "https://api.github.com/repos/$REPO/releases/assets/$asset_id" -o "$tmp/$ASSET"
mkdir "$tmp/unpacked"
tar -xzf "$tmp/$ASSET" -C "$tmp/unpacked"

# --- First-install bootstrap (no-ops when already set up) -------------------
id "$SERVICE_USER" &>/dev/null || useradd --system --no-create-home --groups video "$SERVICE_USER"
mkdir -p "$INSTALL_DIR"

if [[ ! -f "$INSTALL_DIR/bwbAlarmWatcher2.env" ]]; then
  install -o "$SERVICE_USER" -g "$SERVICE_USER" -m 600 \
    "$tmp/unpacked/bwbAlarmWatcher2.env.example" "$INSTALL_DIR/bwbAlarmWatcher2.env"
  echo "NOTE: new env file created - set Api__AuthToken and Tv__IpAddress in $INSTALL_DIR/bwbAlarmWatcher2.env"
fi

if [[ ! -f "/etc/systemd/system/$SERVICE" ]]; then
  install -m 644 "$tmp/unpacked/$SERVICE" "/etc/systemd/system/$SERVICE"
  systemctl daemon-reload
  systemctl enable "$SERVICE"
fi
# -----------------------------------------------------------------------------

systemctl stop "$SERVICE" 2>/dev/null || true

install -o "$SERVICE_USER" -g "$SERVICE_USER" -m 755 \
  "$tmp/unpacked/BwbAlarmWatcher2" "$INSTALL_DIR/BwbAlarmWatcher2"

# appsettings.json only when missing, so local adjustments survive updates
if [[ ! -f "$INSTALL_DIR/appsettings.json" ]]; then
  install -o "$SERVICE_USER" -g "$SERVICE_USER" -m 644 \
    "$tmp/unpacked/appsettings.json" "$INSTALL_DIR/appsettings.json"
fi

# keep this script itself current
if [[ -f "$tmp/unpacked/update.sh" ]]; then
  install -m 755 "$tmp/unpacked/update.sh" "$INSTALL_DIR/update.sh"
fi

echo "$tag" >"$INSTALL_DIR/.installed_version"

systemctl start "$SERVICE"
sleep 2
systemctl --no-pager --lines 5 status "$SERVICE" || true
echo "Update to $tag complete."
