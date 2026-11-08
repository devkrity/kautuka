#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 source "$script_dir/kautuka-install-linux.sh.in"

# Simulate Fedora 45: ICU and dnf are available but OpenSSL 3 is missing.
icu_library_available() { return 0; }
ssl_ready=false
ssl3_library_available() { [[ "$ssl_ready" == true ]]; }
dnf() {
    [[ "$*" == 'install -y openssl3-libs' ]] || return 1
    ssl_ready=true
}

ensure_dotnet_host_libraries
[[ "$ssl_ready" == true ]]

# Once installed, the compatibility package must not be installed again.
dnf() { echo 'unexpected package installation' >&2; return 1; }
ensure_dotnet_host_libraries
echo 'Fedora OpenSSL 3 prerequisite test passed.'
