#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
templates=/usr/share/udica/templates
for file in "$templates/base_container.cil" "$templates/net_container.cil" "$script_dir/kautuka_envoy.cil"; do
  if [[ ! -r "$file" ]]; then
    echo "Missing SELinux policy template: $file" >&2
    exit 1
  fi
done

sudo /usr/bin/semodule -i "$templates/base_container.cil" "$templates/net_container.cil" "$script_dir/kautuka_envoy.cil"
echo "Installed the Kautuka development Envoy SELinux policy."
