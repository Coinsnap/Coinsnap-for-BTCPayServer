#!/usr/bin/env bash
set -euo pipefail

PROJECT="BTCPayServer.Plugins.Coinsnap"

LOCAL_ENV="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/plugin-env.local.sh"
if [ -f "$LOCAL_ENV" ]; then
  source "$LOCAL_ENV"
fi
