#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/generate-linux-installer.sh --output <path> --version <version> [options]

Options:
  --output <path>                Output installer path.
  --version <version>           Default installed version to embed.
  --artifact-url <url>          Default platform artifact URL.
  --feed-base-url <url>         Default feed base URL.
  --manifest-url <url>          Default manifest URL.
  --release-channel <channel>   Default release channel.
  --manifest-public-key <pem>   Manifest public key PEM file to embed.
  --local-feed-defaults         Emit the local-feed wrapper behavior.
  -h, --help                    Show this help.
EOF
}

output_path=''
version=''
artifact_url=''
feed_base_url=''
manifest_url=''
release_channel=''
manifest_public_key=''
local_feed_defaults='false'

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output)
      output_path="$2"
      shift 2
      ;;
    --version)
      version="$2"
      shift 2
      ;;
    --artifact-url)
      artifact_url="$2"
      shift 2
      ;;
    --feed-base-url)
      feed_base_url="$2"
      shift 2
      ;;
    --manifest-url)
      manifest_url="$2"
      shift 2
      ;;
    --release-channel)
      release_channel="$2"
      shift 2
      ;;
    --manifest-public-key)
      manifest_public_key="$2"
      shift 2
      ;;
    --local-feed-defaults)
      local_feed_defaults='true'
      shift
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

if [[ -z "$output_path" || -z "$version" ]]; then
  usage >&2
  exit 1
fi

if [[ -z "$manifest_public_key" && "$local_feed_defaults" != 'true' ]]; then
  echo 'error: --manifest-public-key is required unless --local-feed-defaults is set.' >&2
  exit 1
fi

if [[ -n "$manifest_public_key" && ! -f "$manifest_public_key" ]]; then
  echo "error: manifest public key file not found: $manifest_public_key" >&2
  exit 1
fi


script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
installer_source="$script_dir/kautuka-install-linux.sh.in"
release_verifier_source="$script_dir/verify-linux-release.sh"

{
  echo '#!/usr/bin/env bash'
  if [[ -n "$artifact_url" ]]; then
    printf 'KAUTUKA_DEFAULT_ARTIFACT_URL=%q\n' "$artifact_url"
  fi
  if [[ -n "$feed_base_url" ]]; then
    printf 'KAUTUKA_DEFAULT_FEED_BASE_URL=%q\n' "$feed_base_url"
  fi
  if [[ -n "$manifest_url" ]]; then
    printf 'KAUTUKA_DEFAULT_MANIFEST_URL=%q\n' "$manifest_url"
  fi
  if [[ -n "$release_channel" ]]; then
    printf 'KAUTUKA_DEFAULT_RELEASE_CHANNEL=%q\n' "$release_channel"
  fi
  if [[ -n "$manifest_public_key" ]]; then
    printf 'KAUTUKA_DEFAULT_MANIFEST_PUBLIC_KEY_PEM=%q\n' "$(cat "$manifest_public_key")"
  fi
  printf 'KAUTUKA_DEFAULT_VERSION=%q\n' "$version"
  echo 'KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1'
  sed \
    -e '1d' \
    -e 's/^main() {/base_install_main() {/' \
    -e '/^if \[\[ "\${KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY:-0}" != '\''1'\'' \]\]; then$/,/^fi$/d' \
    "$installer_source"
  cat "$release_verifier_source"

  if [[ "$local_feed_defaults" == 'true' ]]; then
    cat <<'INSTALLER_WRAPPER_EOF'

should_show_help() {
  local arg
  for arg in "$@"; do
    case "$arg" in
      --help|-h)
        return 0
        ;;
    esac
  done

  return 1
}

should_bypass_local_release_defaults() {
  local arg
  for arg in "$@"; do
    case "$arg" in
      --uninstall|--delete-data)
        return 0
        ;;
    esac
  done

  return 1
}

main() {
  if should_show_help "$@"; then
    base_install_main "$@"
    return
  fi

  if should_bypass_local_release_defaults "$@"; then
    base_install_main "$@"
    return
  fi

  KAUTUKA_INSTALL_LOCAL_RELEASE_DEFAULTS=1 base_install_main "$@"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
INSTALLER_WRAPPER_EOF
  else
    cat <<'INSTALLER_WRAPPER_EOF'

main() {
  base_install_main "$@"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
INSTALLER_WRAPPER_EOF
  fi
} | tr -d '\r' > "$output_path"

chmod +x "$output_path"
