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

rm -f "$output_file"
npx -y @anthropic-ai/mcpb validate "$script_dir"
npx -y @anthropic-ai/mcpb pack "$script_dir" "$output_file"

if [[ ! -f "$output_file" ]]; then
    echo "Failed to create MCPB archive at $output_file" >&2
    exit 1
fi

echo "$output_file"