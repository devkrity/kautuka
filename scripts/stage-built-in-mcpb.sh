#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/stage-built-in-mcpb.sh --output <file>

Builds the built-in kautuka-web-tools MCPB bundle to the requested stable output path.
EOF
}

output_file=''

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output)
      output_file="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ -z "$output_file" ]]; then
  usage >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
package_dir="$repo_root/mcps/kautuka-web-tools"

if ! command -v uv >/dev/null 2>&1; then
  echo 'error: uv is required to verify the locked kautuka-web-tools Python dependencies.' >&2
  exit 1
fi
uv lock --check --project "$package_dir"

mkdir -p "$(dirname "$output_file")"
rm -f "$output_file"

npx -y @anthropic-ai/mcpb validate "$package_dir"
npx -y @anthropic-ai/mcpb pack "$package_dir" "$output_file"

if [[ ! -f "$output_file" ]]; then
  echo "error: failed to create MCPB archive at $output_file" >&2
  exit 1
fi

printf '%s\n' "$output_file"
