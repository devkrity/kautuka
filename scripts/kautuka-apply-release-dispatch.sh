#!/usr/bin/env bash
# Stable root-owned entry point. Never select the uncommitted application pointer.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
[[ ${EUID} == 0 ]] || { echo 'error: host tools require root' >&2; exit 1; }
selector=/usr/local/lib/kautuka/host-tools-current
for directory in /usr /usr/local /usr/local/lib /usr/local/lib/kautuka /opt /opt/kautuka /opt/kautuka/manager /opt/kautuka/manager/versions; do
    [[ -d "$directory" && ! -L "$directory" && "$(stat -c %u -- "$directory")" == 0 &&
       -z "$(find "$directory" -maxdepth 0 -perm /022 -print -quit)" ]] || { echo 'error: untrusted host tools directory' >&2; exit 1; }
done
[[ -L "$selector" && "$(stat -c %u -- "$selector")" == 0 ]] || { echo 'error: missing or untrusted host tools selector' >&2; exit 1; }
target="$(readlink -- "$selector")"
[[ "$target" =~ ^/opt/kautuka/manager/versions/[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]] || { echo 'error: invalid host tools generation' >&2; exit 1; }
[[ -d "$target" && ! -L "$target" && "$(stat -c %u -- "$target")" == 0 &&
   -z "$(find "$target" -maxdepth 0 -perm /022 -print -quit)" ]] || { echo 'error: untrusted host tools generation' >&2; exit 1; }
for tool in kautuka-apply-release kautuka-update-recovery; do
    path="$target/$tool"
    [[ -f "$path" && ! -L "$path" && -x "$path" && "$(stat -c %u -- "$path")" == 0 &&
       -z "$(find "$path" -maxdepth 0 -perm /022 -print -quit)" ]] || { echo 'error: untrusted host tool' >&2; exit 1; }
done
exec "$target/kautuka-apply-release" "$@"
