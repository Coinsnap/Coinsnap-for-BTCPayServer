#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$ROOT_DIR/plugin-env.sh"

# -getProperty only evaluates, so build first or the path points at nothing.
dotnet build "$ROOT_DIR/src/$PROJECT/$PROJECT.csproj" -p:Configuration=Debug

TARGET_PATH="$(dotnet build "$ROOT_DIR/src/$PROJECT/$PROJECT.csproj" \
  -p:Configuration=Debug -getProperty:TargetPath)"

printf '{ "DEBUG_PLUGINS": "%s" }' "$TARGET_PATH" \
  > "$ROOT_DIR/submodules/btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The Coinsnap plugin will load when debugging BTCPay Server."
