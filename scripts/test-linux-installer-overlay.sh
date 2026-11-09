#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 source "$script_dir/kautuka-install-linux.sh.in"

test_dir="$(mktemp -d)"
trap 'rm -rf -- "$test_dir"' EXIT
config="$test_dir/storage.conf"
mkdir "$test_dir/graph"
native_rootless_overlay_available() { return 0; }
write_rootless_storage_config "$config" user /home/user /run/user/1 "$test_dir/graph" /run/store
! grep -q mount_program "$config"

# Do not rewrite an existing store, even if support changes.
printf 'existing store configuration\n' > "$config"
native_rootless_overlay_available() { return 1; }
write_rootless_storage_config "$config" user /home/user /run/user/1 "$test_dir/graph" /run/store
grep -Fxq 'existing store configuration' "$config"
printf 'mount_program = "/some/helper"\n' > "$config"
if (write_rootless_storage_config "$config" user /home/user /run/user/1 "$test_dir/graph" /run/store) 2>/dev/null; then
    echo 'an existing mount program was accepted' >&2
    exit 1
fi
rm "$config"
touch "$test_dir/graph/existing-store"
if (write_rootless_storage_config "$config" user /home/user /run/user/1 "$test_dir/graph" /run/store) 2>/dev/null; then
    echo 'an unconfigured existing store was accepted' >&2
    exit 1
fi
rm "$test_dir/graph/existing-store"
if (write_rootless_storage_config "$config" user /home/user /run/user/1 "$test_dir/graph" /run/store) 2>/dev/null; then
    echo 'unsupported native overlay was accepted' >&2
    exit 1
fi
[[ ! -e "$config" ]]
echo 'Rootless overlay selection tests passed.'
