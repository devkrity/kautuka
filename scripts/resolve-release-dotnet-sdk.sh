#!/usr/bin/env bash
set -euo pipefail

global_json="${1:?usage: resolve-release-dotnet-sdk.sh <global.json>}"
if [[ ! -f "$global_json" ]]; then
  echo "error: global.json not found: $global_json" >&2
  exit 1
fi

sdk_version="${KAUTUKA_RELEASE_DOTNET_SDK_VERSION:-}"
if [[ -z "$sdk_version" ]]; then
  sdk_version="$(node -e 'const fs = require("node:fs"); const value = JSON.parse(fs.readFileSync(process.argv[1], "utf8")).sdk?.version; if (typeof value !== "string") process.exit(2); process.stdout.write(value);' "$global_json")"
fi
if [[ ! "$sdk_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo 'error: KAUTUKA_RELEASE_DOTNET_SDK_VERSION must be a numeric SDK version.' >&2
  exit 1
fi
printf '%s\n' "$sdk_version"