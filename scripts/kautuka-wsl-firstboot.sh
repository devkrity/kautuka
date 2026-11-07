#!/usr/bin/env bash
set -euo pipefail
# The Windows installer verifies the signed WSL archive before import. This fixed
# root-owned payload is part of that archive; no first-boot download is allowed.
root=/usr/local/lib/kautuka/firstboot
if [[ "${1:-}" != --locked ]]; then
    # Retain the lease in flock, not in Podman/conmon descendants.
    exec flock --exclusive --nonblock --close /run/kautuka-apply.lock "$0" --locked
fi
[[ "$#" == 1 && "$EUID" == 0 ]] || exit 1
export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$root/installer.sh"
assert_no_unresolved_update
readarray -t metadata < <(python3 - "$root/release.json" <<'PY'
import json,re,sys
m=json.load(open(sys.argv[1]))
v=m['version']; c=m['channel']
if not isinstance(v,str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._+-]*',v) or '..' in v:
    raise SystemExit('invalid first-boot release version')
if c not in ('stable','preview','nightly','dev'): raise SystemExit('invalid first-boot channel')
print(v); print(c)
PY
)
[[ "${#metadata[@]}" == 2 ]] || fail 'invalid first-boot release metadata'
version="${metadata[0]}"
release_channel="${metadata[1]}"
service_user=kautuka
service_group=kautuka
internal_state_root=/var/lib/kautuka
visible_root=/var/lib/kautuka
install_root=/opt/kautuka/manager
manifest_url=''
github_release_candidate=false
skip_enable=false
container_first_boot=true
KAUTUKA_DEFAULT_MANIFEST_PUBLIC_KEY_PEM="$(cat "$root/manifest-public-key.pem")"
payload="$root/payload"
if [[ -e /etc/kautuka/service-scope.json ]]; then
    [[ "$(uninstall_container_layout)" == true ]] || fail 'first-boot retry requires the container deployment contract'
    for service in manager updateservice gateway channelhost credentialshost webhost; do
        [[ "$(readlink -f "/opt/kautuka/$service/current")" == "/opt/kautuka/$service/versions/$version" ]]             || fail 'first-boot retry cannot replace another installed release'
    done
    # Preparation published the contract only after images, identities and all
    # fixed files were ready. Resume activation without regenerating any state.
    ensure_rootless_podman_ready "$service_user" "$service_group" "$internal_state_root" "$(resolve_user_runtime_dir "$service_user")" containers
    install_socket_runtime_dirs "$service_user" containers "$service_group"
    activate_container_appliance "$payload/bin"
else
    install_fresh_container_appliance "$payload"
fi
install -o root -g root -m 0644 /dev/null /etc/kautuka/wsl-firstboot-complete
sync -f /etc/kautuka/wsl-firstboot-complete
