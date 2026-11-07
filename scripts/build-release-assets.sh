#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/build-release-assets.sh --output-root <dir> --private-key <pem> [options]
       scripts/build-release-assets.sh --prewarm-only --output-root <dir> --version <ver> --platform-tarball <tar.gz> [options]

Options:
  --release-id <id>                 Optional metadata-only release id. Default: <release-tag>.
  --release-tag <tag>               Override release tag. Default: v<derived-version>.
  --version <version>               Required canonical manifest/build version in CI.
  --product-version <version>       Required plain SemVer product version in CI.
  --source-sha <sha>                Required full source commit SHA in CI.
  --channel <channel>               Release channel: preview or stable.
  --minimum-updater-version <ver>   Optional minimum updater version for the manifest.
  --manifest-url <url>              Optional manifest URL to preconfigure in the release update source file.
                                    Defaults to <artifact-base-url>/kautuka-platform-release.json when that base is set.
  --artifact-base-url <url>         Absolute URL prefix for artifact `url` fields in the signed platform manifest.
  --signature-url <url>             Optional exact signature URL in the signed release manifest.
  --manifest-public-key <pem>       Required for full builds: public key PEM baked into the prewarmed rootfs.
  --rebuild-images                  Rebuild the cached WSL base rootfs and Linux release build image.
  --prewarm-only                    Layer an existing platform tarball into the cached WSL base rootfs. Skips compile.
  --platform-tarball <tar.gz>       Platform archive for --prewarm-only.

Environment:
  KAUTUKA_RELEASE_CONTAINER_NETWORK_MODE  Optional container network mode override.
  KAUTUKA_RELEASE_CACHE_ROOT              Persistent cache root for pnpm data and cached WSL base rootfs assets.
EOF
}

create_work_dir() {
  local output_root="$1"

  mkdir -p "$output_root"
  mktemp -d "$output_root/.build-release-assets.XXXXXX"
}

invocation_dir="$PWD"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"

# Fail closed on the staged Manager payload before packing the platform tarball.
assert_staged_sidecar_seal() {
  local stage_dir="$1"
  local sidecars_json="$stage_dir/bin/sidecars.json"
  local pkgs_proxy_dockerfile="$stage_dir/bin/sidecar-assets/pkgs-proxy/Dockerfile"

  [[ -f "$sidecars_json" ]] || {
    echo "error: staged sidecars.json missing at $sidecars_json" >&2
    exit 1
  }
  [[ -f "$pkgs_proxy_dockerfile" ]] || {
    echo "error: pkgs-proxy HEALTHCHECK wrapper Dockerfile missing at $pkgs_proxy_dockerfile" >&2
    exit 1
  }
  grep -q 'HEALTHCHECK' "$pkgs_proxy_dockerfile" || {
    echo "error: pkgs-proxy Dockerfile must declare HEALTHCHECK" >&2
    exit 1
  }
  grep -q '127.0.0.1:20101/health' "$pkgs_proxy_dockerfile" || {
    echo "error: pkgs-proxy HEALTHCHECK must probe http://127.0.0.1:20101/health" >&2
    exit 1
  }
  grep -Eq '@sha256:[0-9a-fA-F]{64}' "$pkgs_proxy_dockerfile" || {
    echo "error: pkgs-proxy Dockerfile FROM must be digest-pinned" >&2
    exit 1
  }

  python3 - "$sidecars_json" <<'PY'
import json
import re
import sys

path = sys.argv[1]
catalog = json.load(open(path, encoding="utf-8"))
by_name = {sidecar["name"]: sidecar for sidecar in catalog["sidecars"]}
digest_re = re.compile(r"@sha256:[0-9a-fA-F]{64}$")

oo = by_name["kautuka-openobserve"]
ports = oo.get("ports") or []
if len(ports) != 1:
    raise SystemExit(f"openobserve must publish exactly one port, found {len(ports)}")
if ports[0].get("hostPort") != 20215:
    raise SystemExit(f"openobserve hostPort must be 20215, found {ports[0].get('hostPort')}")
if ports[0].get("hostBindAddress") != "127.0.0.1":
    raise SystemExit(f"openobserve must bind 127.0.0.1, found {ports[0].get('hostBindAddress')}")
if not digest_re.search(oo.get("image") or ""):
    raise SystemExit(f"openobserve image must be digest-pinned, found {oo.get('image')!r}")

proxy = by_name["kautuka-pkgs-proxy"]
if proxy.get("ports"):
    raise SystemExit(f"kautuka-pkgs-proxy must not publish host ports, found {proxy.get('ports')}")
build_context = proxy.get("buildContext") or ""
if "sidecar-assets/pkgs-proxy" not in build_context:
    raise SystemExit(f"kautuka-pkgs-proxy must build from sidecar-assets/pkgs-proxy, found {build_context!r}")
image = proxy.get("image") or ""
if not image.startswith("localhost/"):
    raise SystemExit(f"kautuka-pkgs-proxy image must be localhost-built, found {image!r}")
environment = proxy.get("environment") or {}
if environment.get("PROXY_LISTEN") != ":20101":
    raise SystemExit(f"kautuka-pkgs-proxy PROXY_LISTEN must be :20101, found {environment.get('PROXY_LISTEN')!r}")
if environment.get("PROXY_BASE_URL") != "http://kautuka-pkgs-proxy:20101":
    raise SystemExit(f"kautuka-pkgs-proxy PROXY_BASE_URL must use unpublished alias 20101, found {environment.get('PROXY_BASE_URL')!r}")
forbidden = [name for name in by_name if name in ("kautuka-verdaccio", "kautuka-proxpi")]
if forbidden:
    raise SystemExit(f"retired sidecar names must not remain in the catalog: {forbidden}")

print("staged sidecar seal assertions passed")
PY
}

output_root=''
private_key=''
release_id=''
release_tag=''
version=''
product_version=''
source_sha=''
channel=''
minimum_updater_version=''
manifest_url=''
manifest_public_key=''
artifact_base_url=''
signature_url=''
rebuild_images='false'
prewarm_only='false'
platform_tarball_override=''

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output-root)
      output_root="$2"
      shift 2
      ;;
    --private-key)
      private_key="$2"
      shift 2
      ;;
    --release-id)
      release_id="$2"
      shift 2
      ;;
    --release-tag)
      release_tag="$2"
      shift 2
      ;;
    --version)
      version="$2"
      shift 2
      ;;
    --product-version)
      product_version="$2"
      shift 2
      ;;
    --source-sha)
      source_sha="$2"
      shift 2
      ;;
    --channel)
      channel="$2"
      shift 2
      ;;
    --minimum-updater-version)
      minimum_updater_version="$2"
      shift 2
      ;;
    --manifest-url)
      manifest_url="$2"
      shift 2
      ;;
    --artifact-base-url)
      artifact_base_url="$2"
      shift 2
      ;;
    --signature-url)
      signature_url="$2"
      shift 2
      ;;
    --manifest-public-key)
      manifest_public_key="$2"
      shift 2
      ;;
    --rebuild-images)
      rebuild_images='true'
      shift
      ;;
    --prewarm-only)
      prewarm_only='true'
      shift
      ;;
    --platform-tarball)
      platform_tarball_override="$2"
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

if [[ -n "$private_key" ]]; then
  private_key="$(bash "$repo_root/scripts/resolve-release-key-path.sh" "$invocation_dir" "$private_key")"
fi
if [[ -n "$manifest_public_key" ]]; then
  manifest_public_key="$(bash "$repo_root/scripts/resolve-release-key-path.sh" "$invocation_dir" "$manifest_public_key")"
fi

if [[ "$prewarm_only" == 'true' ]]; then
  if [[ -z "$output_root" || -z "$version" || -z "$platform_tarball_override" ]]; then
    echo 'error: --prewarm-only requires --output-root, --version, and --platform-tarball.' >&2
    usage >&2
    exit 1
  fi
  if [[ ! -f "$platform_tarball_override" ]]; then
    echo "error: platform tarball not found: $platform_tarball_override" >&2
    exit 1
  fi
elif [[ -z "$output_root" || -z "$private_key" ]]; then
  usage >&2
  exit 1
fi

if [[ -z "$manifest_url" && -n "$artifact_base_url" ]]; then
  manifest_url="${artifact_base_url%/}/kautuka-platform-release.json"
fi

if [[ "$prewarm_only" != 'true' && -z "$manifest_public_key" ]]; then
  echo 'error: --manifest-public-key is required for full release builds so appliances can verify signed feeds.' >&2
  exit 1
fi

if [[ -n "$manifest_url" && ! "$manifest_url" =~ ^https?:// ]]; then
  echo 'error: --manifest-url must be an absolute http(s) URL.' >&2
  exit 1
fi


if [[ -n "$signature_url" && ! "$signature_url" =~ ^https?:// ]]; then
  echo 'error: --signature-url must be an absolute http(s) URL.' >&2
  exit 1
fi

if [[ -n "$manifest_public_key" && ! -f "$manifest_public_key" ]]; then
  echo "error: manifest public key file not found: $manifest_public_key" >&2
  exit 1
fi

container_runtime="${KAUTUKA_RELEASE_CONTAINER_RUNTIME:-podman}"
dotnet_sdk_version="$(bash "$repo_root/scripts/resolve-release-dotnet-sdk.sh" "$repo_root/global.json")"
node_build_image="${KAUTUKA_RELEASE_NODE_IMAGE:-}"
read_release_config() {
  node -e 'const fs=require("node:fs");let value=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));for(const key of process.argv[2].split("."))value=value?.[key];if(value===undefined||value===null)process.exit(2);process.stdout.write(String(value))' "$1" "$2"
}
release_toolchain_config="$repo_root/src/images/release-builder/toolchain.json"
rootfs_config="$repo_root/src/images/wsl-rootfs/rootfs.json"
node_version="${KAUTUKA_RELEASE_NODE_VERSION:-$(read_release_config "$release_toolchain_config" 'node.version')}"
node_arch="${KAUTUKA_RELEASE_NODE_ARCH:-x64}"
cache_root="${KAUTUKA_RELEASE_CACHE_ROOT:-$repo_root/.cache/release-build}"
container_network_mode="${KAUTUKA_RELEASE_CONTAINER_NETWORK_MODE:-}"

is_wsl_host() {
  [[ -n "${WSL_DISTRO_NAME:-}" ]] || grep -qi microsoft /proc/version /proc/sys/kernel/osrelease 2>/dev/null
}

if [[ -z "$container_network_mode" && "$container_runtime" == 'podman' ]] && is_wsl_host; then
  container_network_mode='host'
fi

container_network_args=()
if [[ -n "$container_network_mode" ]]; then
  container_network_args=(--network "$container_network_mode")
fi

selinux_mount_relabel=''
if [[ "$container_runtime" == 'podman' ]] && command -v getenforce >/dev/null 2>&1; then
  case "$(getenforce 2>/dev/null || true)" in
    Enforcing|Permissive)
      selinux_mount_relabel='Z'
      ;;
  esac
fi

bind_mount() {
  local source_path="$1"
  local target_path="$2"
  local access_mode="${3:-}"
  local options=''

  if [[ -n "$access_mode" && -n "$selinux_mount_relabel" ]]; then
    options=":${access_mode},${selinux_mount_relabel}"
  elif [[ -n "$access_mode" ]]; then
    options=":${access_mode}"
  elif [[ -n "$selinux_mount_relabel" ]]; then
    options=":${selinux_mount_relabel}"
  fi

  printf '%s:%s%s' "$source_path" "$target_path" "$options"
}

run_build_container() {
  if "$container_runtime" run --rm "${container_network_args[@]}" "$@"; then
    return 0
  fi

  if [[ -z "$container_network_mode" && "$container_runtime" == 'podman' ]]; then
    echo 'warning: container run failed on default networking, retrying once with host networking.' >&2
    "$container_runtime" run --rm --network host "$@"
    return
  fi

  return 1
}

write_provisioned_update_source_document() {
  local destination_path="$1"
  local manifest_url_value="$2"
  local channel_name="$3"
  local use_sudo="${4:-false}"
  local document

  if [[ "$use_sudo" == 'true' ]]; then
    sudo mkdir -p "$(dirname "$destination_path")"
  else
    mkdir -p "$(dirname "$destination_path")"
  fi

  document="$(printf '%s\n' \
    '{' \
    '  "schemaVersion": 1,' \
    "  \"manifestUrl\": \"$manifest_url_value\"" \
    '}')"

  if [[ "$use_sudo" == 'true' ]]; then
    printf '%s\n' "$document" | sudo tee "$destination_path" >/dev/null
  else
    printf '%s\n' "$document" > "$destination_path"
  fi
}

stage_provisioned_update_source() {
  local root_path="$1"
  local owner_spec="${2:-}"

  mkdir -p "$root_path"
  if [[ -n "$owner_spec" ]]; then
    sudo chown root:root "$root_path"
    sudo chmod go-w "$root_path"
  fi

  if [[ -n "$manifest_url" ]]; then
    local destination_path="$root_path/update-source.json"
    write_provisioned_update_source_document "$destination_path" "$manifest_url" "$channel" "$( [[ -n "$owner_spec" ]] && printf 'true' || printf 'false' )"

    # This document contains a public feed URL, not credentials. Do not inherit
    # a restrictive release-builder umask: the runtime must read it after the
    # shared installer establishes its /etc/kautuka traversal group.
    if [[ -n "$owner_spec" ]]; then
      sudo chown "$owner_spec" "$destination_path"
      sudo chmod 0644 "$destination_path"
    else
      chmod 0644 "$destination_path"
    fi
  fi

  if [[ -n "$manifest_public_key" ]]; then
    local key_path="$root_path/manifest-public-key.pem"
    if [[ -n "$owner_spec" ]]; then
      sudo install -D -m 0644 "$manifest_public_key" "$key_path"
      sudo chown root:root "$key_path"
    else
      cp "$manifest_public_key" "$key_path"
      chmod 644 "$key_path"
    fi
  fi
}

container_image_exists() {
  local image_name="$1"

  if [[ "$container_runtime" == 'podman' ]]; then
    "$container_runtime" image exists "$image_name"
    return $?
  fi

  "$container_runtime" image inspect "$image_name" >/dev/null 2>&1
}

hash_file() {
  local file_path="$1"
  sha256sum "$file_path" | awk '{print $1}'
}

compute_hash() {
  printf '%s' "$*" | sha256sum | awk '{print $1}'
}

run_repo_node() {
  env -u NODE_OPTIONS node "$@"
}

stage_oci_image_archives() {
  local dest="$1"
  local inventory_output="$2"
  local allowlist="${3:-$source_root/src/backend/Kautuka.Manager/runtime-images.json}"

  if [[ ! -f "$allowlist" ]]; then
    echo "error: runtime image allowlist not found: $allowlist" >&2
    exit 1
  fi

  local image_rows
  image_rows="$(python3 - "$allowlist" <<'PY'
import json, re, sys
with open(sys.argv[1], encoding="utf-8") as handle:
    data = json.load(handle)
images = data.get("images")
if not isinstance(images, list):
    raise SystemExit("runtime image inventory must contain an images array")
archives = set()
for entry in images:
    if not isinstance(entry, dict):
        raise SystemExit("runtime image inventory contains an invalid entry")
    archive = entry.get("archive")
    if archive is None:
        continue
    values = (entry.get("id"), entry.get("image"), archive)
    if any(not isinstance(value, str) or not value or any(c in value for c in "\t\r\n") for value in values):
        raise SystemExit("runtime image archive entry is invalid")
    if not re.fullmatch(r"[a-z0-9.-]+\.tar", archive) or archive in archives:
        raise SystemExit("runtime image archive name is invalid or duplicated")
    archives.add(archive)
    print("\t".join(values))
PY
)" || { echo 'error: failed to parse runtime image inventory.' >&2; return 1; }
  mkdir -p "$dest"
  while IFS=$'\t' read -r image_id image_ref archive_name; do
    [[ -n "$image_id" && -n "$image_ref" && -n "$archive_name" ]] || continue
    log_phase "Saving first-boot OCI archive ${image_id}"
    local tmp_archive
    tmp_archive="$(mktemp --suffix=.tar)"
    "$container_runtime" pull "$image_ref"
    "$container_runtime" save -o "$tmp_archive" "$image_ref"
    cp "$tmp_archive" "$dest/$archive_name"
    rm -f "$tmp_archive"
    chmod 644 "$dest/$archive_name"
  done <<< "$image_rows"
  local bound_inventory
  bound_inventory="$(mktemp --suffix=.json)"
  if ! python3 "$source_root/scripts/bind-runtime-image-archives.py" \
      --inventory "$allowlist" --archive-root "$dest" --platform linux/amd64 --output "$bound_inventory"; then
    rm -f "$bound_inventory"
    return 1
  fi
  install -m 0644 "$bound_inventory" "$inventory_output"
  rm -f "$bound_inventory"
}

copy_staged_oci_image_archives() {
  local payload="$1" dest="$2" inventory_output="$3" checked
  local inventory="$payload/bin/runtime-images.json"
  [[ -f "$inventory" && -d "$payload/oci-images" ]] || {
    echo 'error: Linux payload lacks the common bound runtime image stage' >&2
    return 1
  }
  checked="$(mktemp --suffix=.json)"
  if ! python3 "$source_root/scripts/bind-runtime-image-archives.py" \
      --inventory "$inventory" --archive-root "$payload/oci-images" --platform linux/amd64 --output "$checked"; then
    rm -f "$checked"
    return 1
  fi
  if ! cmp -s "$inventory" "$checked"; then
    rm -f "$checked"
    echo 'error: packaged runtime image bindings do not match their archives' >&2
    return 1
  fi
  rm -f "$checked"
  sudo mkdir -p "$dest"
  local archive archive_rows
  archive_rows="$(python3 - "$inventory" <<'PY'
import json, sys
for entry in json.load(open(sys.argv[1]))['images']:
    if entry.get('archive') is not None:
        print(entry['archive'])
PY
)" || return 1
  while IFS= read -r archive; do
    [[ -n "$archive" ]] || continue
    sudo install -m 0644 "$payload/oci-images/$archive" "$dest/$archive" || return 1
  done <<< "$archive_rows"
  sudo install -m 0644 "$inventory" "$inventory_output"
}

log_phase() {
  printf '\n==> %s\n' "$1"
}

sudo_keepalive_pid=''

start_sudo_keepalive() {
  if [[ ${EUID} -eq 0 ]] || ! command -v sudo >/dev/null 2>&1; then
    return 0
  fi

  if [[ -n "$sudo_keepalive_pid" ]]; then
    return 0
  fi

  # Prefer a non-interactive check so NOPASSWD hosts (and CI/nohup) do not block on
  # a password prompt. Fall back to interactive sudo -v only when a TTY is present.
  if ! sudo -n true >/dev/null 2>&1; then
    if [[ -t 0 ]]; then
      sudo -v
    else
      echo 'error: sudo credentials are required for the WSL rootfs build, but no TTY is available to prompt.' >&2
      echo 'hint: run this script from an interactive shell, or configure passwordless sudo for the rootfs steps.' >&2
      exit 1
    fi
  fi
  (
    while true; do
      sudo -n true >/dev/null 2>&1 || exit 0
      sleep 30
    done
  ) &
  sudo_keepalive_pid=$!
}

stop_sudo_keepalive() {
  if [[ -z "$sudo_keepalive_pid" ]]; then
    return 0
  fi

  kill "$sudo_keepalive_pid" >/dev/null 2>&1 || true
  wait "$sudo_keepalive_pid" 2>/dev/null || true
  sudo_keepalive_pid=''
}

normalize_owned_paths() {
  if command -v sudo >/dev/null 2>&1; then
    sudo chown -R "$(id -u):$(id -g)" "$@" 2>/dev/null || true
  fi
}

if [[ "${GITHUB_ACTIONS:-false}" == 'true' && ( -z "$version" || -z "$product_version" || -z "$source_sha" || -z "$channel" ) ]]; then
  echo 'error: CI builds require --version, --product-version, --source-sha, and --channel.' >&2
  exit 1
fi
if [[ -z "$version" ]]; then
  product_version="$(sed -n 's:.*<KautukaDevVersion>\(.*\)</KautukaDevVersion>.*:\1:p' "$repo_root/src/Directory.Build.props" | head -n 1 | sed 's/-dev$//')"
  version="${product_version}-dev"
fi
if [[ -z "$product_version" ]]; then
  product_version="${version%%-preview.*}"
fi
if [[ -z "$channel" ]]; then channel='dev'; fi
if [[ -z "$source_sha" ]]; then
  source_sha="$(git -C "$repo_root" rev-parse HEAD)"
fi
if [[ ! "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo 'error: --source-sha must be a full 40-character commit SHA.' >&2
  exit 1
fi
numeric_version="${product_version%%-*}"
if [[ "$channel" != 'preview' && "$channel" != 'stable' && ( "$channel" != 'dev' || "${GITHUB_ACTIONS:-false}" == 'true' ) ]]; then
  echo "error: unsupported channel '$channel'; expected preview or stable in CI." >&2
  exit 1
fi
if [[ "$channel" == 'stable' && "$version" != "$product_version" ]]; then
  echo "error: stable version '$version' must equal product version '$product_version'." >&2
  exit 1
fi
if [[ "$channel" == 'preview' && ! "$version" =~ ^${product_version//./\\.}-preview\.(0|[1-9][0-9]*)$ \
  && !( "${GITHUB_ACTIONS:-false}" != 'true' && "$version" == "${product_version}-dev" ) ]]; then
  echo "error: preview version '$version' must be '$product_version-preview.N'." >&2
  exit 1
fi
release_tag="${release_tag:-v$version}"
release_id="${release_id:-$release_tag}"
published_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
# Docker treats a non-absolute -v source as a named volume rather than a bind
# mount, so every path handed to the container runtime must be absolute.
output_root="$(mkdir -p "$output_root" && cd "$output_root" && pwd)"
release_dir="$output_root/$version"

# A prewarm-only input can be inside the output directory. Preserve it (and
# any existing release files) when rebuilding just the appliance image.
if [[ "$prewarm_only" == 'true' ]]; then
  appliance_output="$release_dir/kautuka-wsl-appliance.tar.gz"
  if [[ "$(realpath -e -- "$platform_tarball_override")" == "$(realpath -m -- "$appliance_output")" \
        || "$platform_tarball_override" -ef "$appliance_output" ]]; then
    echo 'error: prewarm input must be a different file from the output appliance.' >&2
    exit 1
  fi
  for metadata in kautuka-platform-release.json kautuka-platform-release.sig kautuka-release-assets.sha256; do
    if [[ -e "$release_dir/$metadata" ]]; then
      echo "error: prewarm-only cannot replace an appliance beside existing release metadata; use a separate output directory." >&2
      exit 1
    fi
  done
else
  rm -rf "$release_dir"
fi
mkdir -p "$release_dir"

work_dir="$(create_work_dir "$output_root")"
source_root="$work_dir/source"
stage_dir="$work_dir/staging"
release_build_context="$work_dir/compiler-context"
compliance_dir="$work_dir/release-compliance"
compliance_args=(--output-dir "$compliance_dir" --release-tag "$release_tag")
if [[ -n "${KAUTUKA_BASE_IMAGE:-}" ]]; then
  compliance_args+=(--base-image "$KAUTUKA_BASE_IMAGE")
fi
node "$repo_root/scripts/generate-third-party-compliance.mjs" "${compliance_args[@]}"
final_rootfs="$release_dir/kautuka-wsl-appliance.tar.gz"
platform_tarball="$release_dir/kautuka-linux-amd64.tar.gz"
if [[ "$prewarm_only" == 'true' ]]; then
  platform_tarball="$(cd "$(dirname "$platform_tarball_override")" && pwd)/$(basename "$platform_tarball_override")"
  source_root="$repo_root"
fi
pnpm_store_dir="$cache_root/pnpm-store"
nuget_packages_dir="$cache_root/nuget/packages"
rootfs_cache_dir="$cache_root/wsl-rootfs"
rootfs_releasever="$(read_release_config "$rootfs_config" releasever)"
rootfs_arch="$(read_release_config "$rootfs_config" architecture)"
rootfs_image="$(read_release_config "$rootfs_config" composeImage)"
release_build_containerfile="$repo_root/src/images/release-builder/Containerfile"

cleanup_work_dir() {
  local exit_code=$?
  trap - EXIT
  stop_sudo_keepalive

  if [[ "$exit_code" -ne 0 && "${KAUTUKA_RELEASE_RETAIN_FAILED_WORKDIR:-false}" == 'true' && -d "$work_dir" ]]; then
    chmod 700 "$work_dir"
    printf 'Retaining failed release build work directory: %s\n' "$work_dir" >&2
  elif command -v sudo >/dev/null 2>&1; then
    sudo rm -rf "$work_dir" 2>/dev/null || rm -rf "$work_dir"
  else
    rm -rf "$work_dir"
  fi

  exit "$exit_code"
}

trap cleanup_work_dir EXIT

if [[ "$prewarm_only" == 'true' ]]; then
  mkdir -p "$rootfs_cache_dir"
  start_sudo_keepalive
  log_phase "Reusing existing platform tarball for rootfs prewarm"
  printf 'Platform tarball: %s\n' "$platform_tarball"
else
mkdir -p "$source_root"
mkdir -p "$stage_dir/bin" "$stage_dir/services/gateway" "$stage_dir/services/credentialshost" "$stage_dir/services/channelhost" "$stage_dir/services/updateservice" "$stage_dir/services/webhost"
mkdir -p "$pnpm_store_dir" "$nuget_packages_dir" "$rootfs_cache_dir"

start_sudo_keepalive

log_phase "Preparing source snapshot"
bash "$repo_root/scripts/prepare-release-source-snapshot.sh" \
  "$repo_root" \
  "$source_root" \
  "$stage_dir" \
  "$private_key" \
  "$cache_root" \
  "$release_build_context"

cd "$source_root"

if ! command -v "$container_runtime" >/dev/null 2>&1; then
  echo "error: $container_runtime is required to publish Linux appliance binaries with a compatible glibc baseline." >&2
  exit 1
fi

release_build_image_hash="$(compute_hash "$(hash_file "$release_build_containerfile")" "$(hash_file "$release_toolchain_config")" "$(hash_file "$repo_root/src/images/release-builder/npm-tools/package-lock.json")" "$dotnet_sdk_version")"
release_build_image="${KAUTUKA_RELEASE_BUILD_IMAGE:-localhost/kautuka-release-build:${release_build_image_hash:0:16}}"
node_build_image="${node_build_image:-$release_build_image}"

ensure_release_build_image() {
  if [[ -n "${KAUTUKA_RELEASE_BUILD_IMAGE:-}" ]]; then
    if ! container_image_exists "$release_build_image"; then
      printf 'Pulling pinned Linux release toolchain: %s\n' "$release_build_image"
      "$container_runtime" pull "$release_build_image"
    else
      printf 'Using pinned Linux release toolchain: %s\n' "$release_build_image"
    fi
    return 0
  fi

  if [[ "$rebuild_images" != 'true' ]] && container_image_exists "$release_build_image"; then
    printf 'Reusing cached Linux release build image: %s\n' "$release_build_image"
    return 0
  fi

  printf 'Building Linux release build image: %s\n' "$release_build_image"

  mkdir -p "$release_build_context/npm-tools"
  cp "$source_root/src/images/release-builder/npm-tools/package.json" "$release_build_context/npm-tools/package.json"
  cp "$source_root/src/images/release-builder/npm-tools/package-lock.json" "$release_build_context/npm-tools/package-lock.json"

  local build_args=(
    build
    -f "$release_build_containerfile"
    -t "$release_build_image"
    --build-arg "FEDORA_BASE_IMAGE=$(read_release_config "$release_toolchain_config" baseImage)"
    --build-arg "DOTNET_SDK_VERSION=$dotnet_sdk_version"
    --build-arg "NODE_VERSION=$node_version"
    --build-arg "NODE_SHA256=$(read_release_config "$release_toolchain_config" node.linuxX64Sha256)"
    --build-arg "PNPM_VERSION=$(read_release_config "$release_toolchain_config" pnpmVersion)"
    --build-arg "UV_VERSION=$(read_release_config "$release_toolchain_config" uv.version)"
    --build-arg "UV_SHA256=$(read_release_config "$release_toolchain_config" uv.linuxX64Sha256)"
  )

  if [[ "$rebuild_images" == 'true' ]]; then
    build_args+=(--no-cache)
  fi

  build_args+=("$release_build_context")

  "$container_runtime" "${build_args[@]}"
}

log_phase "Ensuring Linux release build image"
ensure_release_build_image
if ! run_build_container "$release_build_image" dotnet --list-sdks | awk '{print $1}' | grep -Fxq "$dotnet_sdk_version"; then
  echo "error: release builder does not contain required .NET SDK $dotnet_sdk_version; refresh the pinned builder image." >&2
  exit 1
fi

log_phase "Building frontend assets"
run_build_container \
  -e VITE_KAUTUKA_VERSION="$version" \
  -e VITE_KAUTUKA_SOURCE_SHA="${source_sha:0:7}" \
  -v "$(bind_mount "$pnpm_store_dir" '/pnpm-store')" \
  -v "$(bind_mount "$source_root" '/src')" \
  -w /src \
  "$node_build_image" \
  bash -lc "
    set -euo pipefail
    command -v node >/dev/null
    command -v pnpm >/dev/null
    # Non-interactive: allow pnpm to replace an incompatible/stale modules dir
    # (e.g. Windows node_modules that leaked into the Linux source snapshot).
    export CI=true
    pnpm config set store-dir /pnpm-store
    pnpm --dir src/frontend install --frozen-lockfile --prefer-offline
    pnpm --dir src/frontend/web run build
  "

normalize_owned_paths "$pnpm_store_dir"

built_in_mcpb_stage_dir="$work_dir/prebuilt-assets"
built_in_mcpb_path="$built_in_mcpb_stage_dir/kautuka-web-tools.mcpb"
mail_aot_dir="$work_dir/m365-mail-aot"
google_mail_aot_dir="$work_dir/google-mail-aot"

mkdir -p "$built_in_mcpb_stage_dir" "$mail_aot_dir" "$google_mail_aot_dir"

log_phase "Building built-in MCP bundles"
run_build_container \
  -v "$(bind_mount "$source_root" '/src')" \
  -v "$(bind_mount "$built_in_mcpb_stage_dir" '/out')" \
  -w /src \
  "$node_build_image" \
  bash -lc "
    set -euo pipefail
    command -v node >/dev/null
    command -v uv >/dev/null
    command -v mcpb >/dev/null
    bash scripts/stage-built-in-mcpb.sh --output /out/kautuka-web-tools.mcpb
  "

normalize_owned_paths "$built_in_mcpb_stage_dir"

log_phase "Publishing Linux host binaries"
run_build_container \
  -e KAUTUKA_RELEASE_INFORMATIONAL_VERSION="${version}+${source_sha:0:7}" \
  -e KAUTUKA_RELEASE_VERSION="$version" \
  -e KAUTUKA_RELEASE_NUMERIC_VERSION="$numeric_version" \
  -e VITE_KAUTUKA_VERSION="$version" \
  -e VITE_KAUTUKA_SOURCE_SHA="${source_sha:0:7}" \
  -e KAUTUKA_BUILT_IN_MCPB_PATH=/prebuilt-assets/kautuka-web-tools.mcpb \
  -e NUGET_PACKAGES=/nuget/packages \
  -v "$(bind_mount "$source_root" '/src')" \
  -v "$(bind_mount "$stage_dir" '/out')" \
  -v "$(bind_mount "$built_in_mcpb_stage_dir" '/prebuilt-assets' 'ro')" \
  -v "$(bind_mount "$mail_aot_dir" '/m365-mail-aot')" \
  -v "$(bind_mount "$google_mail_aot_dir" '/google-mail-aot')" \
  -v "$(bind_mount "$nuget_packages_dir" '/nuget/packages')" \
  -w /src \
  "$release_build_image" \
  bash -lc '
    set -euo pipefail
    publish_host() {
      local project_path="$1"
      local output_path="$2"

      echo "Publishing ${project_path}..."

      dotnet publish "$project_path" \
        --maxcpucount \
        --runtime linux-x64 \
        --self-contained \
        -p:KautukaReleaseArtifactBuild=true \
        -p:KautukaBuiltInMcpBundlePath="$KAUTUKA_BUILT_IN_MCPB_PATH" \
        -p:PublishSingleFile=true \
        -p:InformationalVersion="$KAUTUKA_RELEASE_INFORMATIONAL_VERSION" \
        -p:Version="$KAUTUKA_RELEASE_VERSION" \
        -p:AssemblyVersion="$KAUTUKA_RELEASE_NUMERIC_VERSION" \
        -p:FileVersion="$KAUTUKA_RELEASE_NUMERIC_VERSION" \
        -p:DebugType=none \
        -c Release \
        -o "$output_path"
    }

    copy_manager_runtime_assets() {
      local output_path="$1"

      mkdir -p "$output_path/sandbox" "$output_path/sidecar-assets"
      cp src/backend/Kautuka.Manager/sidecars.json "$output_path/sidecars.json"
      install -D -m 0644 src/images/kautuka-base/Dockerfile "$output_path/sandbox/Dockerfile"
      install -D -m 0644 src/images/kautuka-pkgs-proxy/Dockerfile "$output_path/sidecar-assets/pkgs-proxy/Dockerfile"
      install -D -m 0644 src/images/openobserve/README.txt "$output_path/sidecar-assets/openobserve/README.txt"
    }

    copy_gateway_runtime_assets() {
      local output_path="$1"

      mkdir -p "$output_path/built-in-mcpb"
      cp "$KAUTUKA_BUILT_IN_MCPB_PATH" "$output_path/built-in-mcpb/kautuka-web-tools.mcpb"
    }

    dotnet publish src/backend/connectors/m365-mail/kautuka-m365-mail.csproj \
      --maxcpucount \
      --runtime linux-x64 \
      --self-contained \
      -c Release \
      -o /m365-mail-aot
    dotnet publish src/backend/connectors/google-mail/kautuka-google-mail.csproj \
      --maxcpucount \
      --runtime linux-x64 \
      --self-contained \
      -c Release \
      -o /google-mail-aot

    publish_host src/backend/Kautuka.Manager/Kautuka.Manager.csproj /out/bin
    copy_manager_runtime_assets /out/bin
    publish_host src/backend/Kautuka.Gateway/Kautuka.Gateway.csproj /out/services/gateway
    copy_gateway_runtime_assets /out/services/gateway
    publish_host src/backend/Kautuka.CredentialsHost/Kautuka.CredentialsHost.csproj /out/services/credentialshost
    publish_host src/backend/Kautuka.ChannelHost/Kautuka.ChannelHost.csproj /out/services/channelhost
    publish_host src/backend/Kautuka.UpdateService/Kautuka.UpdateService.csproj /out/services/updateservice
    publish_host src/backend/Kautuka.WebHost/Kautuka.WebHost.csproj /out/services/webhost
  '

normalize_owned_paths "$stage_dir" "$nuget_packages_dir"

if command -v sudo >/dev/null 2>&1; then
  sudo chown -R "$(id -u):$(id -g)" "$stage_dir" 2>/dev/null || true
fi

log_phase "Packing Microsoft 365 mail connector"
mail_pack_dir="$work_dir/m365-mail-pack"
rm -rf "$mail_pack_dir"
mkdir -p "$mail_pack_dir"
cp "$mail_aot_dir/kautuka-m365-mail" "$mail_pack_dir/kautuka-m365-mail"
cp "$source_root/src/backend/connectors/m365-mail/manifest.json" "$mail_pack_dir/manifest.json"
chmod 755 "$mail_pack_dir/kautuka-m365-mail"
run_build_container \
  -v "$(bind_mount "$mail_pack_dir" '/pack')" \
  -v "$(bind_mount "$built_in_mcpb_stage_dir" '/out')" \
  -w /pack \
  "$node_build_image" \
  bash -lc "
    set -euo pipefail
    command -v node >/dev/null
    mcpb validate /pack
    mcpb pack /pack /out/kautuka-m365-mail.mcpb
  "
normalize_owned_paths "$built_in_mcpb_stage_dir"
mkdir -p "$stage_dir/services/gateway/built-in-mcpb"
cp "$built_in_mcpb_stage_dir/kautuka-m365-mail.mcpb" "$stage_dir/services/gateway/built-in-mcpb/kautuka-m365-mail.mcpb"

log_phase "Packing Google mail connector"
google_mail_pack_dir="$work_dir/google-mail-pack"
rm -rf "$google_mail_pack_dir"
mkdir -p "$google_mail_pack_dir"
cp "$google_mail_aot_dir/kautuka-google-mail" "$google_mail_pack_dir/kautuka-google-mail"
cp "$source_root/src/backend/connectors/google-mail/manifest.json" "$google_mail_pack_dir/manifest.json"
chmod 755 "$google_mail_pack_dir/kautuka-google-mail"
run_build_container \
  -v "$(bind_mount "$google_mail_pack_dir" '/pack')" \
  -v "$(bind_mount "$built_in_mcpb_stage_dir" '/out')" \
  -w /pack \
  "$node_build_image" \
  bash -lc "
    set -euo pipefail
    command -v node >/dev/null
    mcpb validate /pack
    mcpb pack /pack /out/kautuka-google-mail.mcpb
  "
normalize_owned_paths "$built_in_mcpb_stage_dir"
cp "$built_in_mcpb_stage_dir/kautuka-google-mail.mcpb" "$stage_dir/services/gateway/built-in-mcpb/kautuka-google-mail.mcpb"

log_phase "Assembling Linux platform tarball"
mv "$stage_dir/bin/Kautuka.Manager" "$stage_dir/bin/kautuka-manager"
mv "$stage_dir/services/gateway/Kautuka.Gateway" "$stage_dir/services/gateway/kautuka-gateway"
mv "$stage_dir/services/credentialshost/Kautuka.CredentialsHost" "$stage_dir/services/credentialshost/kautuka-credentialshost"
mv "$stage_dir/services/channelhost/Kautuka.ChannelHost" "$stage_dir/services/channelhost/kautuka-channelhost"
mv "$stage_dir/services/updateservice/Kautuka.UpdateService" "$stage_dir/services/updateservice/kautuka-updateservice"
mv "$stage_dir/services/webhost/Kautuka.WebHost" "$stage_dir/services/webhost/kautuka-webhost"
printf '%s\n' 'kautuka-maintenance-page-v1' > "$stage_dir/services/webhost/.maintenance-page-v1"
printf '%s\n' "$version" > "$stage_dir/bin/release-version.txt"
printf '%s\n' "$release_id" > "$stage_dir/bin/release-id.txt"

if [[ -n "$manifest_url" ]]; then
  provisioned_config_dir="$stage_dir/provisioning/etc/kautuka"
  mkdir -p "$provisioned_config_dir"
  stage_provisioned_update_source "$provisioned_config_dir"
fi

cp "$source_root/scripts/kautuka-apply-release.sh" "$stage_dir/bin/kautuka-apply-release"
cp "$source_root/scripts/kautuka-update-recovery.py" "$stage_dir/bin/kautuka-update-recovery"
cp "$source_root/scripts/kautuka-runtime-images.py" "$stage_dir/bin/kautuka-runtime-images"
chmod +x "$stage_dir/bin/kautuka-manager" \
  "$stage_dir/bin/kautuka-apply-release" \
  "$stage_dir/bin/kautuka-update-recovery" \
  "$stage_dir/services/gateway/kautuka-gateway" \
  "$stage_dir/services/credentialshost/kautuka-credentialshost" \
  "$stage_dir/services/channelhost/kautuka-channelhost" \
  "$stage_dir/services/updateservice/kautuka-updateservice" \
  "$stage_dir/services/webhost/kautuka-webhost"
mkdir -p "$stage_dir/services/webhost/wwwroot"
cp -a "$source_root/src/frontend/web/dist/." "$stage_dir/services/webhost/wwwroot/"

assert_staged_sidecar_seal "$stage_dir"

mkdir -p "$stage_dir/share/doc/kautuka/licenses"
cp "$compliance_dir/THIRD_PARTY_NOTICES.txt" "$compliance_dir/THIRD_PARTY_COMPONENTS.json" "$compliance_dir/FRONTEND_DEPENDENCIES.json" "$stage_dir/share/doc/kautuka/"
cp "$compliance_dir/licenses/"* "$stage_dir/share/doc/kautuka/licenses/"

# Stage and bind once, before either platform artifact is assembled. WSL copies
# these exact bytes from the Linux payload instead of independently pulling.
log_phase "Staging common runtime image archives"
stage_oci_image_archives "$stage_dir/oci-images" "$stage_dir/bin/runtime-images.json"
python3 "$source_root/scripts/package-runtime-images.py" --source-root "$source_root" \
  --payload "$stage_dir" --work-root "$work_dir" --runtime "$container_runtime" \
  --version "$version" --numeric-version "$numeric_version" --source-sha "$source_sha"
cp "$stage_dir/bin/runtime-images.json" "$release_dir/runtime-images.bound.json"

mkdir -p "$(dirname "$platform_tarball")"
tar -czf "$platform_tarball" -C "$stage_dir" .
fi

# drvfs/Windows mounts often reject chmod; invoke via bash instead of relying on +x.
chmod +x "$source_root/scripts/build-wsl-rootfs.sh" 2>/dev/null || true

rootfs_cache_key="$(compute_hash "$(hash_file "$source_root/scripts/build-wsl-rootfs.sh")" "$rootfs_releasever" "$rootfs_arch" "$rootfs_image")"
base_rootfs="$rootfs_cache_dir/kautuka-wsl-rootfs-${rootfs_cache_key:0:16}.tar.gz"

log_phase "Preparing cached WSL base rootfs"
if [[ -n "${KAUTUKA_RELEASE_WSL_ROOTFS_PATH:-}" ]]; then
  if [[ ! -s "$KAUTUKA_RELEASE_WSL_ROOTFS_PATH" ]]; then
    echo "error: pinned WSL rootfs archive is missing or empty: $KAUTUKA_RELEASE_WSL_ROOTFS_PATH" >&2
    exit 1
  fi
  cp "$KAUTUKA_RELEASE_WSL_ROOTFS_PATH" "$base_rootfs"
  printf 'Using the reviewed WSL base rootfs from %s\n' "$KAUTUKA_RELEASE_WSL_ROOTFS_PATH"
elif [[ "$rebuild_images" == 'true' || ! -f "$base_rootfs" ]]; then
  printf 'Building cached WSL base rootfs: %s\n' "$base_rootfs"
  bash "$source_root/scripts/build-wsl-rootfs.sh" \
    --releasever "$rootfs_releasever" \
    --arch "$rootfs_arch" \
    --image "$rootfs_image" \
    --container-runtime "$container_runtime" \
    --output "$base_rootfs"
else
  printf 'Reusing cached WSL base rootfs: %s\n' "$base_rootfs"
fi

prewarm_rootfs() {
  local rootfs_input="$1"
  local rootfs_output="$2"
  local manager_extract_dir="$work_dir/manager"
  local rootfs_dir="$work_dir/rootfs"

  # Host-side hardening after extract/pack. Rootless Podman bind-mounts can leave
  # privileged paths owned by the invoking user even when the container chowned them.
  harden_rootfs_sudo_bits() {
    local root="$1"
    local sudo_bin
    for sudo_bin in "$root/usr/bin/sudo" "$root/usr/sbin/sudo"; do
      if [[ -e "$sudo_bin" || -L "$sudo_bin" ]]; then
        sudo chown root:root "$sudo_bin"
        sudo chmod 4755 "$sudo_bin"
      fi
    done
    for helper in "$root/usr/bin/newuidmap" "$root/usr/bin/newgidmap"; do
      if [[ -e "$helper" || -L "$helper" ]]; then
        sudo chown root:root "$helper"
        sudo chmod 4755 "$helper"
      fi
    done
    if [[ -e "$root/etc/sudo.conf" ]]; then
      sudo chown root:root "$root/etc/sudo.conf"
      sudo chmod 0644 "$root/etc/sudo.conf"
    fi
    if [[ -e "$root/etc/sudoers" ]]; then
      sudo chown root:root "$root/etc/sudoers"
      sudo chmod 0440 "$root/etc/sudoers"
    fi
    if [[ -d "$root/etc/sudoers.d" ]]; then
      sudo chown root:root "$root/etc/sudoers.d"
      sudo chmod 0750 "$root/etc/sudoers.d"
      sudo find "$root/etc/sudoers.d" -type f -exec chown root:root {} + -exec chmod 0440 {} +
    fi
  }

  if command -v sudo >/dev/null 2>&1; then
    sudo rm -rf "$manager_extract_dir" "$rootfs_dir" 2>/dev/null || true
  fi
  rm -rf "$manager_extract_dir" "$rootfs_dir"
  mkdir -p "$manager_extract_dir" "$rootfs_dir"
  sudo tar -xzf "$rootfs_input" -C "$rootfs_dir"
  for required in usr/bin/openssl usr/bin/python3 usr/bin/base64; do
    [[ -x "$rootfs_dir/$required" ]] || {
      echo "error: WSL rootfs is missing required update verification executable /$required; refresh the pinned rootfs image" >&2
      exit 1
    }
  done
  harden_rootfs_sudo_bits "$rootfs_dir"
  tar -xzf "$platform_tarball" -C "$manager_extract_dir"

  # Stage the identical Linux payload. Real rootless initialization and key
  # generation happen in the imported distro, never in a fake chroot service bus.
  local firstboot="$rootfs_dir/usr/local/lib/kautuka/firstboot"
  sudo install -d -o root -g root -m 0755 "$firstboot" "$rootfs_dir/etc/kautuka" \
    "$rootfs_dir/etc/systemd/system/multi-user.target.wants"
  sudo cp -a "$manager_extract_dir" "$firstboot/payload"
  copy_staged_oci_image_archives "$manager_extract_dir" "$firstboot/payload/oci-images" "$firstboot/payload/bin/runtime-images.json"
  sudo chown -R root:root "$firstboot"
  sudo chmod -R go-w "$firstboot"
  sudo install -m 0755 "$source_root/scripts/kautuka-wsl-firstboot.sh" "$firstboot/start"
  sudo install -m 0644 "$source_root/scripts/kautuka-install-linux.sh.in" "$firstboot/installer.sh"
  [[ -n "$manifest_public_key" ]] || { echo 'error: WSL first boot requires the trusted release public key' >&2; exit 1; }
  sudo install -m 0644 "$manifest_public_key" "$firstboot/manifest-public-key.pem"
  python3 - "$version" "$channel" <<'PYMETA' | sudo tee "$firstboot/release.json" >/dev/null
import json,sys
print(json.dumps(dict(version=sys.argv[1],channel=sys.argv[2])))
PYMETA
  sudo chmod 0644 "$firstboot/release.json"
  if [[ -n "$manifest_url" ]]; then
    stage_provisioned_update_source "$rootfs_dir/etc/kautuka" '0:0'
  fi
  # The base image owns the dedicated account. Its home is immutable ancestry;
  # the shared installer creates only the required writable children on boot.
  sudo chown root:root "$rootfs_dir/var/lib/kautuka"
  sudo chmod 0755 "$rootfs_dir/var/lib/kautuka"
  sudo install -m 0644 "$source_root/scripts/kautuka-wsl-firstboot.service" \
    "$rootfs_dir/etc/systemd/system/kautuka-wsl-firstboot.service"
  sudo ln -s ../kautuka-wsl-firstboot.service \
    "$rootfs_dir/etc/systemd/system/multi-user.target.wants/kautuka-wsl-firstboot.service"
  # Preserve the producer's bound inventory byte-for-byte for final signing.
  sudo install -m 0644 "$firstboot/payload/bin/runtime-images.json" "$release_dir/runtime-images.bound.json"
  sudo install -D -m 0644 "$manager_extract_dir/LICENSE" "$rootfs_dir/usr/share/licenses/kautuka/LICENSE"
  sudo install -D -m 0644 "$manager_extract_dir/NOTICE" "$rootfs_dir/usr/share/licenses/kautuka/NOTICE"

  harden_rootfs_sudo_bits "$rootfs_dir"

  sudo mkdir -p "$rootfs_dir/usr/share/doc/kautuka/licenses"
  sudo cp "$compliance_dir/THIRD_PARTY_NOTICES.txt" "$compliance_dir/THIRD_PARTY_COMPONENTS.json" "$compliance_dir/FRONTEND_DEPENDENCIES.json" "$rootfs_dir/usr/share/doc/kautuka/"
  sudo cp "$compliance_dir/licenses/"* "$rootfs_dir/usr/share/doc/kautuka/licenses/"

  sudo mkdir -p "$(dirname "$rootfs_output")"
  sudo tar --numeric-owner --xattrs --acls -czf "$rootfs_output" -C "$rootfs_dir" .
  sudo chown "$(id -u):$(id -g)" "$rootfs_output"

  # Fail closed on the published appliance archive, not only the work tree.
  # awk must drain the whole listing; exiting early closes the pipe and makes tar
  # die with a write error, which pipefail would report as a build failure.
  local packed_sudo_owner
  packed_sudo_owner="$(tar -tvf "$rootfs_output" | awk '/\.\/usr\/bin\/sudo$/ { owner = $2 } END { print owner }')"
  [[ "$packed_sudo_owner" == '0/0' ]] || {
    echo "error: appliance sudo owner must be 0/0 (found ${packed_sudo_owner:-missing})" >&2
    exit 1
  }
  # No appliance-specific credentials or old host core units may be baked in.
  python3 "$source_root/scripts/test-wsl-staged-rootfs.py" "$rootfs_output"

}

log_phase "Prewarming WSL appliance rootfs"
prewarm_rootfs "$base_rootfs" "$final_rootfs"

if [[ "$prewarm_only" == 'true' ]]; then
  printf 'Prewarmed appliance rootfs written to %s\n' "$final_rootfs"
  ls -lh "$final_rootfs"
  exit 0
fi

manifest_args=()
if [[ -n "$minimum_updater_version" ]]; then
  manifest_args+=(--minimum-updater-version "$minimum_updater_version")
fi
if [[ -n "$signature_url" ]]; then
  manifest_args+=(--signature-url "$signature_url")
fi

log_phase "Generating signed release manifest"
run_repo_node "$source_root/scripts/generate-release-manifest.mjs" \
  --output-dir "$release_dir" \
  --private-key "$private_key" \
  --release-id "$release_id" \
  --release-tag "$release_tag" \
  --version "$version" \
  --source-sha "$source_sha" \
  --channel "$channel" \
  --artifact-base-url "$artifact_base_url" \
  --published-at "$published_at" \
  --runtime-images "$release_dir/runtime-images.bound.json" \
  "${manifest_args[@]}"

(
  cd "$release_dir"
  sha256sum kautuka-linux-amd64.tar.gz kautuka-wsl-appliance.tar.gz kautuka-platform-release.json kautuka-platform-release.sig > kautuka-release-assets.sha256
)

printf 'Release artifacts written to %s\n' "$release_dir"
printf 'Manifest: %s\n' "$release_dir/kautuka-platform-release.json"
