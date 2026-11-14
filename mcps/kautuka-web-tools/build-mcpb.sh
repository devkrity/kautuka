#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"

if ! command -v npx >/dev/null 2>&1; then
    echo "npx is required to build MCPB bundles with the reference CLI" >&2
    exit 1
fi

version="$(sed -n 's/^version = "\([^"]*\)"$/\1/p' "$script_dir/pyproject.toml" | head -n 1)"
if [[ -z "$version" ]]; then
    echo "Could not determine package version from pyproject.toml" >&2
    exit 1
fi

output_dir="$repo_root/artifacts/mcpb"
output_file="$output_dir/kautuka-web-tools-$version.mcpb"

mkdir -p "$output_dir"

temp_file="$output_dir/.kautuka-web-tools-${version}.$(node -p "require('node:crypto').randomBytes(12).toString('hex')").tmp.mcpb"
trap 'rm -f "$temp_file"' EXIT
npx -y @anthropic-ai/mcpb validate "$script_dir"
npx -y @anthropic-ai/mcpb pack "$script_dir" "$temp_file"

if [[ ! -s "$temp_file" ]]; then
    echo "Failed to create MCPB archive at $temp_file" >&2
    exit 1
fi
node "$repo_root/scripts/validate-mcpb-archive.mjs" "$temp_file"

mv -f -- "$temp_file" "$output_file"

echo "$output_file"
