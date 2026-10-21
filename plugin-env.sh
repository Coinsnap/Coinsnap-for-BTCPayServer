#!/usr/bin/env bash
set -euo pipefail

PROJECT="BTCPayServer.Plugins.Coinsnap"

LOCAL_ENV="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/plugin-env.local.sh"
[ -f "$LOCAL_ENV" ] && source "$LOCAL_ENV"
