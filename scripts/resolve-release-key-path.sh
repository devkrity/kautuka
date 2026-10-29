#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo 'usage: resolve-release-key-path.sh <invoking-directory> <path>' >&2
  exit 2
fi

invoking_directory="$(realpath -e -- "$1")"
key_path="$2"

if [[ "$key_path" != /* ]]; then
  key_path="$invoking_directory/$key_path"
fi

realpath -m -- "$key_path"
