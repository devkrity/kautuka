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
  --preview-manifest-url <url>     Default preview channel manifest URL.
  --stable-manifest-url <url>   Default stable channel manifest URL.
  --catalog-manifest-url <url>  Default catalog manifest URL.
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
preview_manifest_url=''
stable_manifest_url=''
catalog_manifest_url=''
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
    --preview-manifest-url)
      preview_manifest_url="$2"
      shift 2
      ;;
    --stable-manifest-url)
      stable_manifest_url="$2"
      shift 2
      ;;
    --catalog-manifest-url)
      catalog_manifest_url="$2"
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

if [[ -n "$catalog_manifest_url" && ! "$catalog_manifest_url" =~ ^https?:// ]]; then
  echo 'error: --catalog-manifest-url must be an absolute http(s) URL.' >&2
  exit 1
fi

if [[ -n "$preview_manifest_url" && ! "$preview_manifest_url" =~ ^https?:// ]]; then
  echo 'error: --preview-manifest-url must be an absolute http(s) URL.' >&2
  exit 1
fi

if [[ -n "$stable_manifest_url" && ! "$stable_manifest_url" =~ ^https?:// ]]; then
  echo 'error: --stable-manifest-url must be an absolute http(s) URL.' >&2
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
  if [[ -n "$preview_manifest_url" ]]; then
    printf 'KAUTUKA_DEFAULT_PREVIEW_MANIFEST_URL=%q\n' "$preview_manifest_url"
  fi
  if [[ -n "$stable_manifest_url" ]]; then
    printf 'KAUTUKA_DEFAULT_STABLE_MANIFEST_URL=%q\n' "$stable_manifest_url"
  fi
  if [[ -n "$catalog_manifest_url" ]]; then
    printf 'KAUTUKA_DEFAULT_CATALOG_MANIFEST_URL=%q\n' "$catalog_manifest_url"
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

normalize_feed_base_url() {
  local raw_url="$1"
  local normalized="${raw_url%/}"

  case "$normalized" in
    */manifest)
      printf '%s\n' "${normalized%/manifest}"
      ;;
    */kautuka-platform-release.json)
      printf '%s\n' "${normalized%/kautuka-platform-release.json}"
      ;;
    *)
      printf '%s\n' "$normalized"
      ;;
  esac
}

resolve_feed_base_url_arg() {
  local args=("$@")
  local index=0

  while (( index < ${#args[@]} )); do
    case "${args[index]}" in
      --feed-base-url)
        if (( index + 1 < ${#args[@]} )); then
          printf '%s\n' "${args[index + 1]}"
          return 0
        fi
        ;;
      --feed-base-url=*)
        printf '%s\n' "${args[index]#--feed-base-url=}"
        return 0
        ;;
    esac
    index=$((index + 1))
  done

  return 1
}

resolve_service_user_arg() {
  local args=("$@")
  local index=0

  while (( index < ${#args[@]} )); do
    case "${args[index]}" in
      --service-user)
        if (( index + 1 < ${#args[@]} )); then
          printf '%s\n' "${args[index + 1]}"
          return 0
        fi
        ;;
      --service-user=*)
        printf '%s\n' "${args[index]#--service-user=}"
        return 0
        ;;
    esac
    index=$((index + 1))
  done

  return 1
}

resolve_service_group_arg() {
  local args=("$@")
  local index=0

  while (( index < ${#args[@]} )); do
    case "${args[index]}" in
      --service-group)
        if (( index + 1 < ${#args[@]} )); then
          printf '%s\n' "${args[index + 1]}"
          return 0
        fi
        ;;
      --service-group=*)
        printf '%s\n' "${args[index]#--service-group=}"
        return 0
        ;;
    esac
    index=$((index + 1))
  done

  return 1
}

manifest_url_from_feed_base_url() {
  local normalized_base
  normalized_base="$(normalize_feed_base_url "$1")"
  printf '%s/manifest\n' "$normalized_base"
}

ensure_local_release_defaults() {
  local effective_manifest_url="${1:-$KAUTUKA_DEFAULT_MANIFEST_URL}"
  local effective_catalog_manifest_url="${2:-$KAUTUKA_DEFAULT_CATALOG_MANIFEST_URL}"
  local service_user="${3:-kautuka}"
  local service_group="${4:-$service_user}"
  local policy_file_path='/var/lib/kautuka/updateService/update-runtime-policy.json'
  local effective_preview_manifest_url=''
  local effective_stable_manifest_url=''

  mkdir -p "$(dirname "$policy_file_path")"
  printf '%s\n' \
    '{' \
    '  "schemaVersion": 1,' \
    "  \"releaseChannel\": \"$KAUTUKA_DEFAULT_RELEASE_CHANNEL\"" \
    '}' > "$policy_file_path"
  chmod 640 "$policy_file_path"
  if id "$service_user" >/dev/null 2>&1 && getent group "$service_group" >/dev/null 2>&1; then
    chown "$service_user:$service_group" "$policy_file_path"
  fi

  case "$KAUTUKA_DEFAULT_RELEASE_CHANNEL" in
    stable)
      effective_stable_manifest_url="$effective_manifest_url"
      ;;
    *)
      effective_preview_manifest_url="$effective_manifest_url"
      ;;
  esac

  write_update_source_document \
    "$effective_manifest_url" \
    "$effective_preview_manifest_url" \
    "$effective_stable_manifest_url" \
    "$effective_catalog_manifest_url" \
    "$service_user" \
    "$service_group"
  write_trusted_manifest_public_key "$KAUTUKA_DEFAULT_MANIFEST_PUBLIC_KEY_PEM" "$service_user" "$service_group"
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

  local feed_base_url
  local effective_manifest_url
  local effective_catalog_manifest_url
  local service_user
  local service_group
  feed_base_url="$(resolve_feed_base_url_arg "$@" 2>/dev/null || true)"
  service_user="$(resolve_service_user_arg "$@" 2>/dev/null || true)"
  service_group="$(resolve_service_group_arg "$@" 2>/dev/null || true)"
  if [[ -z "$feed_base_url" && -n "${KAUTUKA_DEFAULT_FEED_BASE_URL:-}" ]]; then
    feed_base_url="$KAUTUKA_DEFAULT_FEED_BASE_URL"
  fi

  if [[ -z "$service_user" ]]; then
    service_user='kautuka'
  fi

  if [[ -z "$service_group" ]]; then
    service_group="$service_user"
  fi

  if [[ -n "$feed_base_url" ]]; then
    effective_manifest_url="$(manifest_url_from_feed_base_url "$feed_base_url")"
    effective_catalog_manifest_url="$(catalog_manifest_url_from_feed_base_url "$feed_base_url")"
  else
    effective_manifest_url="${KAUTUKA_DEFAULT_MANIFEST_URL:-}"
    effective_catalog_manifest_url="${KAUTUKA_DEFAULT_CATALOG_MANIFEST_URL:-}"
  fi

  ensure_local_release_defaults "$effective_manifest_url" "$effective_catalog_manifest_url" "$service_user" "$service_group"
  base_install_main "$@"
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
