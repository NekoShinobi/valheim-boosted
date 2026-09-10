#!/usr/bin/env bash
# Download CI-only game references; do not start the server or publish its files.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 /path/to/steamcmd.sh /path/to/server-install" >&2
  exit 2
fi
steamcmd=$(realpath "$1")
install_dir=$(realpath -m "$2")
mkdir -p "$install_dir"

# Let a fresh SteamCMD finish its self-update before requesting game content.
timeout --kill-after=15s 3m "$steamcmd" +quit

for attempt in 1 2 3; do
  echo "Installing Valheim build references (attempt $attempt/3)"
  status=0
  timeout --kill-after=15s 7m "$steamcmd" \
    +@ShutdownOnFailedCommand 1 +@NoPromptForPassword 1 \
    +@sSteamCmdForcePlatformType linux \
    +force_install_dir "$install_dir" +login anonymous \
    +app_update 896660 validate +quit || status=$?

  if [[ $status -eq 0 && -s "$install_dir/valheim_server_Data/Managed/assembly_valheim.dll" ]]; then
    echo "Valheim dedicated-server build references downloaded."
    exit 0
  fi

  echo "SteamCMD attempt $attempt failed (exit $status, or required assembly missing)." >&2
  if [[ $attempt -lt 3 ]]; then
    # Reuse the initialized SteamCMD and downloaded data on the next invocation.
    sleep 10
  fi
done

echo "Unable to install Valheim app 896660 after 3 attempts; refusing to build with missing or stale references." >&2
exit 1
