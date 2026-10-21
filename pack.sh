#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$ROOT_DIR/plugin-env.sh"

CONFIGURATION="${CONFIGURATION:-Release}"
PROJECT_FILE="$ROOT_DIR/src/$PROJECT/$PROJECT.csproj"
PACKER_PROJECT="$ROOT_DIR/submodules/btcpayserver/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj"
TARGET_DIR="$ROOT_DIR/src/$PROJECT/bin/$CONFIGURATION/net10.0"
PACKER_OUTPUT="$ROOT_DIR/submodules/btcpayserver/BTCPayServer.PluginPacker/bin/Release/net10.0/BTCPayServer.PluginPacker.dll"

if [[ "${NO_RESTORE:-0}" != "1" ]]; then
  dotnet restore "$PROJECT_FILE"
  dotnet restore "$PACKER_PROJECT"
fi

dotnet build "$PROJECT_FILE" --configuration "$CONFIGURATION" --no-restore \
  --disable-build-servers --maxcpucount:1
dotnet build "$PACKER_PROJECT" --configuration Release --no-restore \
  --disable-build-servers --maxcpucount:1
dotnet "$PACKER_OUTPUT" "$TARGET_DIR" "$PROJECT" "$ROOT_DIR/artifacts"

echo "Package and checksums are in $ROOT_DIR/artifacts/$PROJECT."
