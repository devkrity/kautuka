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

assert_packed_appliance_network_seal() {
  local rootfs_output="$1"
  local kautuka_uid="${2:-}"

  local sockets_conf
  sockets_conf="$(tar -xOf "$rootfs_output" ./usr/lib/tmpfiles.d/kautuka-sockets.conf 2>/dev/null || true)"
  [[ -n "$sockets_conf" ]] || {
    echo "error: packed appliance missing ./usr/lib/tmpfiles.d/kautuka-sockets.conf" >&2
    exit 1
  }
  local required_socket_dir
  for required_socket_dir in manager updateservice gateway credentialshost channelhost; do
    printf '%s\n' "$sockets_conf" | grep -q "d /run/kautuka/${required_socket_dir} " || {
      echo "error: packed kautuka-sockets.conf missing directory for ${required_socket_dir}" >&2
      exit 1
    }
  done

  if [[ -n "$kautuka_uid" ]]; then
    local restart_dropin
    restart_dropin="$(tar -xOf "$rootfs_output" "./etc/systemd/system/user@${kautuka_uid}.service.d/20-kautuka-restart.conf" 2>/dev/null || true)"
    [[ -n "$restart_dropin" ]] || {
      echo "error: packed appliance missing user@${kautuka_uid} 20-kautuka-restart.conf drop-in" >&2
      exit 1
    }
    printf '%s\n' "$restart_dropin" | grep -q 'TimeoutStopSec=5' || {
      echo "error: packed user@ drop-in must set TimeoutStopSec=5" >&2
      exit 1
    }
    printf '%s\n' "$restart_dropin" | grep -q 'Restart=on-failure' || {
      echo "error: packed user@ drop-in must set Restart=on-failure" >&2
      exit 1
    }
  fi

  echo "packed appliance network seal assertions passed"
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
node_build_image="${KAUTUKA_RELEASE_NODE_IMAGE:-quay.io/fedora/fedora:44}"
node_version="${KAUTUKA_RELEASE_NODE_VERSION:-26.3.0}"
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

  if [[ -n "$manifest_url" ]]; then
    local destination_path="$root_path/update-source.json"
    write_provisioned_update_source_document "$destination_path" "$manifest_url" "$channel" "$( [[ -n "$owner_spec" ]] && printf 'true' || printf 'false' )"

    if [[ -n "$owner_spec" ]]; then
      sudo chown "$owner_spec" "$destination_path"
    fi
  fi

  if [[ -n "$manifest_public_key" ]]; then
    local key_path="$root_path/manifest-public-key.pem"
    if [[ -n "$owner_spec" ]]; then
      sudo install -D -m 0644 "$manifest_public_key" "$key_path"
      sudo chown "$owner_spec" "$key_path"
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
  local rootfs_dir="$1"
  local allowlist="$source_root/src/backend/Kautuka.Manager/runtime-images.json"
  local dest="$rootfs_dir/var/lib/kautuka/oci-images"

  if [[ ! -f "$allowlist" ]]; then
    echo "error: runtime image allowlist not found: $allowlist" >&2
    exit 1
  fi

  sudo mkdir -p "$dest"
  while IFS=$'\t' read -r image_id image_ref archive_name; do
    [[ -n "$image_id" && -n "$image_ref" && -n "$archive_name" ]] || continue
    log_phase "Saving first-boot OCI archive ${image_id}"
    local tmp_archive
    tmp_archive="$(mktemp --suffix=.tar)"
    "$container_runtime" pull "$image_ref"
    "$container_runtime" save -o "$tmp_archive" "$image_ref"
    sudo cp "$tmp_archive" "$dest/$archive_name"
    rm -f "$tmp_archive"
    sudo chmod 644 "$dest/$archive_name"
  done < <(python3 - "$allowlist" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as handle:
    data = json.load(handle)
for entry in data.get("images", []):
    print("\t".join((entry["id"], entry["image"], entry["archive"])))
PY
)
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

rm -rf "$release_dir"
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
rootfs_releasever='44'
rootfs_arch='amd64'
rootfs_image='quay.io/fedora/fedora:44'
release_build_containerfile="$repo_root/scripts/release-build-image.Containerfile"

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

release_build_image_hash="$(compute_hash "$(hash_file "$release_build_containerfile")" "$dotnet_sdk_version")"
release_build_image="localhost/kautuka-release-build:${release_build_image_hash:0:16}"

ensure_release_build_image() {
  if [[ "$rebuild_images" != 'true' ]] && container_image_exists "$release_build_image"; then
    printf 'Reusing cached Linux release build image: %s\n' "$release_build_image"
    return 0
  fi

  printf 'Building Linux release build image: %s\n' "$release_build_image"

  local build_args=(
    build
    -f "$release_build_containerfile"
    -t "$release_build_image"
    --build-arg "DOTNET_SDK_VERSION=$dotnet_sdk_version"
  )

  if [[ "$rebuild_images" == 'true' ]]; then
    build_args+=(--no-cache)
  fi

  build_args+=("$release_build_context")

  "$container_runtime" "${build_args[@]}"
}

log_phase "Ensuring Linux release build image"
ensure_release_build_image

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
    dnf install -y --setopt=install_weak_deps=False curl tar xz libatomic git
    curl -fSL \"https://nodejs.org/dist/v${node_version}/node-v${node_version}-linux-${node_arch}.tar.xz\" -o /tmp/node.tar.xz
    tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz
    rm -f /tmp/node.tar.xz
    if ! command -v corepack >/dev/null 2>&1; then
      npm install --global --force corepack@latest
    fi
    corepack enable
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
    dnf install -y --setopt=install_weak_deps=False curl tar xz libatomic
    curl -fSL \"https://nodejs.org/dist/v${node_version}/node-v${node_version}-linux-${node_arch}.tar.xz\" -o /tmp/node.tar.xz
    tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz
    rm -f /tmp/node.tar.xz
    uv_version=0.13.6
    uv_archive=uv-x86_64-unknown-linux-gnu.tar.gz
    uv_sha256=f29c3e94ffabd7e6f3ba521dba169a9b7ec12100bf3257087f0f24f6123f3645
    curl --fail --show-error --location --retry 3 --retry-all-errors --silent \
      \"https://releases.astral.sh/github/uv/releases/download/\${uv_version}/\${uv_archive}\" \
      -o /tmp/uv.tar.gz
    printf '%s  %s\n' \"\$uv_sha256\" /tmp/uv.tar.gz | sha256sum --check --status
    tar -xzf /tmp/uv.tar.gz -C /tmp
    install -m 0755 \"/tmp/uv-x86_64-unknown-linux-gnu/uv\" /usr/local/bin/uv
    rm -rf /tmp/uv.tar.gz /tmp/uv-x86_64-unknown-linux-gnu
    uv --version | grep -Fq \"uv \${uv_version} \"
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
      cp src/backend/Kautuka.Manager/SandboxAssets/Dockerfile "$output_path/sandbox/Dockerfile"
      cp -a src/backend/Kautuka.Manager/SidecarAssets/. "$output_path/sidecar-assets/"
    }

    copy_gateway_runtime_assets() {
      local output_path="$1"

      mkdir -p "$output_path/built-in-mcpb"
      cp "$KAUTUKA_BUILT_IN_MCPB_PATH" "$output_path/built-in-mcpb/kautuka-web-tools.mcpb"
    }

    dotnet publish src/backend/connectors/m365-mail/kautuka-m365-mail.csproj \
      --runtime linux-x64 \
      --self-contained \
      -c Release \
      -o /m365-mail-aot
    dotnet publish src/backend/connectors/google-mail/kautuka-google-mail.csproj \
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
    dnf install -y --setopt=install_weak_deps=False curl tar xz libatomic
    curl -fSL \"https://nodejs.org/dist/v${node_version}/node-v${node_version}-linux-${node_arch}.tar.xz\" -o /tmp/node.tar.xz
    tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz
    rm -f /tmp/node.tar.xz
    npx -y @anthropic-ai/mcpb validate /pack
    npx -y @anthropic-ai/mcpb pack /pack /out/kautuka-m365-mail.mcpb
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
    dnf install -y --setopt=install_weak_deps=False curl tar xz libatomic
    curl -fSL \"https://nodejs.org/dist/v${node_version}/node-v${node_version}-linux-${node_arch}.tar.xz\" -o /tmp/node.tar.xz
    tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz
    rm -f /tmp/node.tar.xz
    npx -y @anthropic-ai/mcpb validate /pack
    npx -y @anthropic-ai/mcpb pack /pack /out/kautuka-google-mail.mcpb
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
printf '%s\n' "$version" > "$stage_dir/bin/release-version.txt"
printf '%s\n' "$release_id" > "$stage_dir/bin/release-id.txt"

if [[ -n "$manifest_url" ]]; then
  provisioned_config_dir="$stage_dir/provisioning/etc/kautuka"
  mkdir -p "$provisioned_config_dir"
  stage_provisioned_update_source "$provisioned_config_dir"
fi

cp "$source_root/scripts/kautuka-apply-release.sh" "$stage_dir/bin/kautuka-apply-release"
chmod +x "$stage_dir/bin/kautuka-manager" \
  "$stage_dir/bin/kautuka-apply-release" \
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

mkdir -p "$(dirname "$platform_tarball")"
tar -czf "$platform_tarball" -C "$stage_dir" .
fi

# drvfs/Windows mounts often reject chmod; invoke via bash instead of relying on +x.
chmod +x "$source_root/scripts/build-wsl-rootfs.sh" 2>/dev/null || true

rootfs_cache_key="$(compute_hash "$(hash_file "$source_root/scripts/build-wsl-rootfs.sh")" "$rootfs_releasever" "$rootfs_arch" "$rootfs_image")"
base_rootfs="$rootfs_cache_dir/kautuka-wsl-rootfs-${rootfs_cache_key:0:16}.tar.gz"

log_phase "Preparing cached WSL base rootfs"
if [[ "$rebuild_images" == 'true' || ! -f "$base_rootfs" ]]; then
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
  harden_rootfs_sudo_bits "$rootfs_dir"
  tar -xzf "$platform_tarball" -C "$manager_extract_dir"

  install_host_service() {
    local service_key="$1"
    local executable_name="$2"
    local source_root="$3"

    [[ -d "$source_root" ]] || return 0

    local install_root="$rootfs_dir/opt/kautuka/${service_key}"
    local version_root="$install_root/versions/$version"
    local current_link="$install_root/current"
    local unit_path="$user_unit_dir/kautuka-${service_key}.service"
    local wants_path="$user_unit_dir/kautuka.target.wants/kautuka-${service_key}.service"
    local log_dir="$visible_root/logs/${service_key}"
    local extra_env=''
    local unit_after='network.target'

    sudo rm -rf "$version_root"
    sudo mkdir -p "$version_root" "$log_dir"
    sudo cp -a "$source_root/." "$version_root/"
    sudo chmod +x "$version_root/$executable_name"
    sudo ln -sfn "versions/$version" "$current_link"

    if [[ "$service_key" == 'webhost' ]]; then
      unit_after='kautuka-gateway.service'
    elif [[ "$service_key" == 'gateway' ]]; then
      unit_after='kautuka-credentialshost.service kautuka-manager.service'
    elif [[ "$service_key" == 'channelhost' ]]; then
      unit_after='kautuka-gateway.service'
    fi

    local service_unit_lines=(
      '[Unit]'
      "Description=Kautuka ${service_key}"
      "After=${unit_after}"
      'PartOf=kautuka.target'
      ''
      '[Service]'
      'Type=exec'
      "WorkingDirectory=/opt/kautuka/${service_key}/current"
      "ExecStart=/opt/kautuka/${service_key}/current/${executable_name}"
      "Environment=Logging__FileLogDirectory=/var/lib/kautuka/logs/${service_key}"
    )

    if [[ -n "$extra_env" ]]; then
      service_unit_lines+=("$extra_env")
    fi

    service_unit_lines+=(
      'Restart=always'
      'RestartSec=5'
      'TimeoutStopSec=70'
    )
    # UpdateService must sudo the root helper. Match native installer:
    # install_host_service_user leaves no_new_privileges empty for updateservice.
    if [[ "$service_key" != 'updateservice' ]]; then
      service_unit_lines+=('NoNewPrivileges=true')
    else
      # Keep apply-release children alive if this unit stops mid-apply.
      service_unit_lines+=('KillMode=process')
    fi
    service_unit_lines+=(
      ''
      '[Install]'
      'WantedBy=kautuka.target'
    )

    printf '%s\n' "${service_unit_lines[@]}" | sudo tee "$unit_path" >/dev/null

    sudo mkdir -p "$(dirname "$wants_path")"
    sudo ln -sfn "../kautuka-${service_key}.service" "$wants_path"
  }

  rootfs_uid() {
    awk -F: -v n="$1" '$1 == n { print $3 }' "$rootfs_dir/etc/passwd"
  }

  rootfs_gid() {
    awk -F: -v n="$1" '$1 == n { print $3 }' "$rootfs_dir/etc/group"
  }

  # Match installer chown_tree_excluding: never roll channel security material
  # or isolated CredentialsHost state into the broad kautuka tree ownership.
  chown_tree_excluding() {
    local root="$1"
    local owner="$2"
    local excluded_root="$3"
    local containers_root="$root/.local/share/containers"
    local credentials_root="$root/credentials"
    local credentials_state_root="$root/credentialsHost"

    [[ -d "$root" ]] || return 0
    sudo chown "$owner" "$root"
    sudo find "$root" -mindepth 1 \
      \( -path "$excluded_root" -o -path "$excluded_root/*" \
         -o -path "$containers_root" -o -path "$containers_root/*" \
         -o -path "$credentials_root" -o -path "$credentials_root/*" \
         -o -path "$credentials_state_root" -o -path "$credentials_state_root/*" \) -prune \
      -o -exec chown -h "$owner" {} +
  }

  require_rootfs_owner_mode() {
    local path="$1"
    local expected="$2"
    local actual
    actual="$(sudo stat -c '%u:%g:%a' "$path")"
    if [[ "$actual" != "$expected" ]]; then
      echo "error: rootfs ownership/mode mismatch for $path (expected $expected, found $actual)" >&2
      exit 1
    fi
  }

  ensure_rootfs_system_account() {
    local user="$1"
    if ! awk -F: -v n="$user" '$1 == n { found=1 } END { exit !found }' "$rootfs_dir/etc/passwd"; then
      sudo chroot "$rootfs_dir" groupadd --system "$user"
      sudo chroot "$rootfs_dir" useradd --system --gid "$user" --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$user"
    fi
  }

  install_system_channel_service() {
    local service_key="$1"
    local service_description="$2"
    local executable_name="$3"
    local source_root="$4"
    local runtime_user="$5"
    local install_root="$rootfs_dir/opt/kautuka/${service_key}"
    local version_root="$install_root/versions/$version"
    local current_link="$install_root/current"
    local unit_path="$rootfs_dir/etc/systemd/system/kautuka-${service_key}.service"
    local log_dir="$visible_root/logs/${service_key}"
    local state_write_path="$internal_state_root"
    local dependencies='network-online.target'
    local supplementary_groups=''
    local material_environment=''

    [[ -d "$source_root" ]] || return 0
    ensure_rootfs_system_account "$runtime_user"

    sudo rm -rf "$version_root"
    sudo mkdir -p "$version_root" "$log_dir" "$rootfs_dir/etc/systemd/system/multi-user.target.wants"
    sudo cp -a "$source_root/." "$version_root/"
    sudo chmod +x "$version_root/$executable_name"
    sudo ln -sfn "versions/$version" "$current_link"

    if [[ "$service_key" == 'channelhost' ]]; then
      state_write_path="$internal_state_root/channelHost"
      material_environment="Environment=Kautuka__ChannelGateway__ProductionMaterial__ChannelPrivateRoot=/var/lib/kautuka/security/channel/channel-private
Environment=Kautuka__ChannelGateway__ProductionMaterial__ChannelTrustRoot=/var/lib/kautuka/security/channel/channel-trust"
    elif [[ "$service_key" == 'gateway' ]]; then
      dependencies='network-online.target kautuka-channelhost.service'
      supplementary_groups='SupplementaryGroups=kautuka kautuka-credentials-client'
      material_environment="Environment=Kautuka__ChannelGateway__ProductionMaterial__GatewayPrivateRoot=/var/lib/kautuka/security/channel/gateway-private
Environment=Kautuka__ChannelGateway__ProductionMaterial__GatewayTrustRoot=/var/lib/kautuka/security/channel/gateway-trust"
    fi

    sudo mkdir -p "$state_write_path"

    {
      printf '%s\n' \
        '[Unit]' \
        "Description=Kautuka ${service_description}" \
        'Wants=network-online.target' \
        'Requires=kautuka-channelhost.service' \
        "After=${dependencies}" \
        '' \
        '[Service]' \
        'Type=exec' \
        "User=${runtime_user}" \
        "Group=${runtime_user}"
      if [[ -n "$supplementary_groups" ]]; then
        printf '%s\n' "$supplementary_groups"
      fi
      printf '%s\n' \
        "WorkingDirectory=/opt/kautuka/${service_key}/current" \
        "ExecStart=/opt/kautuka/${service_key}/current/${executable_name}" \
        'Environment=ASPNETCORE_ENVIRONMENT=Production' \
        'Environment=Kautuka__Mode=prod' \
        'Environment=Kautuka__ContainerStateRoot=/var/lib/kautuka' \
        "Environment=Logging__FileLogDirectory=/var/lib/kautuka/logs/${service_key}"
      if [[ -n "$material_environment" ]]; then
        printf '%s\n' "$material_environment"
      fi
      printf '%s\n' \
        'UMask=0077' \
        'NoNewPrivileges=true' \
        'PrivateTmp=true' \
        'ProtectHome=true' \
        'ProtectSystem=strict' \
        "ReadWritePaths=${state_write_path#${rootfs_dir}} ${log_dir#${rootfs_dir}} /run/kautuka/${service_key}" \
        'Restart=always' \
        'RestartSec=5' \
        'TimeoutStopSec=70' \
        '' \
        '[Install]' \
        'WantedBy=multi-user.target'
    } | sudo tee "$unit_path" >/dev/null

    if [[ "$service_key" == 'channelhost' ]]; then
      sudo sed -i '/^Requires=kautuka-channelhost.service$/d' "$unit_path"
    fi

    sudo ln -sfn "../kautuka-${service_key}.service" "$rootfs_dir/etc/systemd/system/multi-user.target.wants/kautuka-${service_key}.service"
  }

  # A client can only traverse a 0750 socket directory if it belongs to the owning group.
  # The group is always a dedicated "-client" group, never the server's own primary group:
  # joining that would also hand over its group-readable state, which is precisely what
  # the pre-existing kautuka-credentials-client split exists to prevent.
  grant_socket_group_membership() {
    local account="$1"
    local group="$2"
    grep -q "^${account}:" "$rootfs_dir/etc/passwd" || return 0
    grep -q "^${group}:" "$rootfs_dir/etc/group" \
      || sudo chroot "$rootfs_dir" groupadd --system "$group"
    sudo chroot "$rootfs_dir" usermod --append --groups "$group" "$account"
  }

  # The services address each other over Unix sockets rather than loopback ports, because
  # every running WSL2 distro shares a single network namespace and no bind address can
  # make a port private to the appliance. /run is tmpfs, so the directories are declared
  # to systemd-tmpfiles and recreated each boot.
  #
  # RuntimeDirectory= cannot do this job: manager, updateservice and webhost are
  # user-scope units, whose RuntimeDirectory lands under /run/user/<uid>, and that
  # directory is mode 0700 so the system-scope services could never traverse into it.
  install_socket_runtime_dirs() {
    local tmpfiles_path="$rootfs_dir/usr/lib/tmpfiles.d/kautuka-sockets.conf"
    sudo mkdir -p "$(dirname "$tmpfiles_path")"

    {
      printf '%s\n' \
        '# Managed by build-release-assets.sh.' \
        '# One directory per service, owned by that service at 2750, so only the owner and' \
        '# members of its client group can reach the socket inside. The setgid bit makes' \
        '# bind() give the socket that client group instead of the service own group, which' \
        '# is what lets the socket itself be 0660 rather than world-writable.' \
        'd /run/kautuka 0755 root root -'

      # service_key:owner:traversal_group
      local entry
      local service_key
      local owner
      local group
      for entry in \
        'manager:kautuka:kautuka-manager-client' \
        'updateservice:kautuka:kautuka-updateservice-client' \
        'gateway:kautuka-gateway:kautuka-gateway-client' \
        'credentialshost:kautuka-credentials:kautuka-credentials-client' \
        'channelhost:kautuka-channelhost:kautuka-channelhost-client'; do
        IFS=':' read -r service_key owner group <<<"$entry"
        if grep -q "^${owner}:" "$rootfs_dir/etc/passwd"; then
          printf 'd /run/kautuka/%s 2750 %s %s -\n' "$service_key" "$owner" "$group"
        fi
      done
    } | sudo tee "$tmpfiles_path" >/dev/null

    # One grant per client-to-server edge: WebHost (kautuka) reaches Gateway, and Gateway
    # reaches Manager, UpdateService and ChannelHost. Gateway is already in
    # kautuka-credentials-client, which covers CredentialsHost.
    #
    # Manager and UpdateService both run as kautuka, so their sockets could have been left
    # in the kautuka group, which Gateway is already a member of. They are not, because
    # that group is those services' own primary group: it also carries their
    # group-readable state under /var/lib/kautuka, so relying on it would widen Gateway's
    # reach well past the two sockets it actually needs. UpdateService still reaches the
    # manager socket as its owner, so only Gateway needs the grant.
    grant_socket_group_membership kautuka kautuka-gateway-client
    grant_socket_group_membership kautuka-gateway kautuka-manager-client
    grant_socket_group_membership kautuka-gateway kautuka-updateservice-client
    grant_socket_group_membership kautuka-gateway kautuka-channelhost-client
  }

  manager_content_root="$manager_extract_dir"
  if [[ -d "$manager_extract_dir/bin" ]]; then
    manager_content_root="$manager_extract_dir/bin"
  fi

  kautuka_uid="$(awk -F: '$1 == "kautuka" { print $3 }' "$rootfs_dir/etc/passwd")"
  kautuka_gid="$(awk -F: '$1 == "kautuka" { print $4 }' "$rootfs_dir/etc/passwd")"
  if [[ -z "$kautuka_uid" || -z "$kautuka_gid" ]]; then
    echo 'error: failed to resolve kautuka uid/gid from rootfs passwd.' >&2
    exit 1
  fi

  install_root="$rootfs_dir/opt/kautuka/manager"
  version_root="$install_root/versions/$version"
  current_link="$install_root/current"
  visible_root="$rootfs_dir/var/lib/kautuka"
  internal_state_root="$rootfs_dir/var/lib/kautuka"
  linger_dir="$rootfs_dir/var/lib/systemd/linger"
  user_unit_dir="$rootfs_dir/var/lib/kautuka/.config/systemd/user"
  service_unit_path="$user_unit_dir/kautuka-manager.service"
  wants_dir="$user_unit_dir/default.target.wants"
  update_policy_file="$rootfs_dir/var/lib/kautuka/updateService/update-runtime-policy.json"

  sudo rm -rf "$version_root"
  sudo mkdir -p "$version_root" "$visible_root/logs/manager" "$visible_root/workspaces" "$visible_root/oci-images" "$internal_state_root/manager" "$wants_dir" "$linger_dir" "$user_unit_dir/kautuka.target.wants" "$rootfs_dir/etc/kautuka"
  sudo cp -a "$manager_content_root/." "$version_root/"
  sudo chmod +x "$version_root/kautuka-manager"
  sudo ln -sfn "versions/$version" "$current_link"
  sudo touch "$linger_dir/kautuka"
  stage_oci_image_archives "$rootfs_dir"
  sudo chown -R "$kautuka_uid:$kautuka_gid" "$visible_root/oci-images"
  sudo chmod 0750 "$visible_root/oci-images"

  # TimeoutStopSec bounds only the teardown of the user manager itself, so the
  # manager unit keeps its own 70s for targeted stops and apply-release. Without
  # it, podman retries transient scopes that systemd refuses once exit.target is
  # queued, the stop outlasts the window WSL allows, and the abandoned cgroup
  # makes the next start fail with EBUSY on cgroup reuse (systemd/systemd#41278).
  # Restart= is the safety net for any teardown that still fails to complete:
  # user@.service is static with no Restart=, so one leak would otherwise strand
  # the user-scope services for a whole boot while the system units still report
  # healthy. The leaked cgroup is empty, so a single retry claims it.
  user_manager_dropin_dir="$rootfs_dir/etc/systemd/system/user@$kautuka_uid.service.d"
  sudo mkdir -p "$user_manager_dropin_dir"
  printf '%s\n' \
    '[Unit]' \
    'StartLimitIntervalSec=60' \
    'StartLimitBurst=5' \
    '' \
    '[Service]' \
    'Restart=on-failure' \
    'RestartSec=5' \
    'TimeoutStopSec=5' | sudo tee "$user_manager_dropin_dir/20-kautuka-restart.conf" >/dev/null
  sudo chmod 644 "$user_manager_dropin_dir/20-kautuka-restart.conf"

  printf '%s\n' \
    '{' \
    '  "schemaVersion": 2,' \
    '  "username": "kautuka",' \
    '  "target": "kautuka.target",' \
    '  "userUnits": ["kautuka-manager.service", "kautuka-updateservice.service", "kautuka-webhost.service"],' \
    '  "systemUnits": ["kautuka-gateway.service", "kautuka-channelhost.service", "kautuka-credentialshost.service"]' \
    '}' | sudo tee "$rootfs_dir/etc/kautuka/service-scope.json" >/dev/null
  sudo chmod 644 "$rootfs_dir/etc/kautuka/service-scope.json"

  sudo mkdir -p "$(dirname "$update_policy_file")"
  printf '%s\n' \
    '{' \
    '  "schemaVersion": 1,' \
    "  \"releaseChannel\": \"$channel\"" \
    '}' | sudo tee "$update_policy_file" >/dev/null
  sudo chmod 640 "$update_policy_file"

  if [[ -n "$manifest_url" || -n "$manifest_public_key" ]]; then
    stage_provisioned_update_source "$rootfs_dir/etc/kautuka" "$kautuka_uid:$kautuka_gid"
  fi

  printf '%s\n' \
    '[Unit]' \
    'Description=Kautuka Manager' \
    'After=network.target' \
    'PartOf=kautuka.target' \
    '' \
    '[Service]' \
    'Type=exec' \
    'WorkingDirectory=/opt/kautuka/manager/current' \
    'ExecStart=/opt/kautuka/manager/current/kautuka-manager' \
    'Environment=Logging__FileLogDirectory=/var/lib/kautuka/logs/manager' \
    'Restart=always' \
    'RestartSec=5' \
    'Delegate=yes' \
    'KillMode=process' \
    'TasksMax=infinity' \
    'TimeoutStopSec=70' \
    '' \
    '[Install]' \
    'WantedBy=kautuka.target' | sudo tee "$service_unit_path" >/dev/null

  local installed_units=(kautuka-manager.service)
  if [[ -d "$manager_extract_dir/services/credentialshost" ]]; then
    credential_group='kautuka-credentials-client'
    credential_user='kautuka-credentials'
    sudo chroot "$rootfs_dir" groupadd --system "$credential_group"
    sudo chroot "$rootfs_dir" useradd --system --no-create-home --home-dir /var/lib/kautuka/credentials --shell /usr/sbin/nologin "$credential_user"
    sudo chroot "$rootfs_dir" usermod --append --groups "$credential_group" "$credential_user"
    sudo chroot "$rootfs_dir" usermod --append --groups "$credential_group" kautuka
    credential_install_root="$rootfs_dir/opt/kautuka/credentialshost"
    credential_version_root="$credential_install_root/versions/$version"
    sudo mkdir -p "$credential_version_root" "$rootfs_dir/var/lib/kautuka/credentials/vault" "$rootfs_dir/var/lib/kautuka/credentials/transport" "$rootfs_dir/var/lib/kautuka/credentialsHost" "$rootfs_dir/var/lib/kautuka/logs/credentialshost"
    sudo cp -a "$manager_extract_dir/services/credentialshost/." "$credential_version_root/"
    sudo chmod +x "$credential_version_root/kautuka-credentialshost"
    sudo ln -sfn "versions/$version" "$credential_install_root/current"
    sudo mkdir -p "$rootfs_dir/etc/systemd/system/multi-user.target.wants"
    printf '%s\n' \
      '[Unit]' \
      'Description=Kautuka Credentials Host' \
      'After=network.target' \
      'Before=kautuka-gateway.service' \
      '' \
      '[Service]' \
      'Type=exec' \
      "User=$credential_user" \
      "Group=$credential_group" \
      'UMask=0027' \
      'WorkingDirectory=/opt/kautuka/credentialshost/current' \
      'ExecStart=/opt/kautuka/credentialshost/current/kautuka-credentialshost' \
      'Environment=Kautuka__Mode=prod' \
      'Environment=Kautuka__ContainerStateRoot=/var/lib/kautuka' \
      'Environment=Logging__FileLogDirectory=/var/lib/kautuka/logs/credentialshost' \
      'Restart=always' \
      'RestartSec=5' \
      'NoNewPrivileges=true' \
      'PrivateTmp=true' \
      'ProtectSystem=strict' \
      'ReadWritePaths=/var/lib/kautuka/credentials /var/lib/kautuka/credentialsHost /var/lib/kautuka/logs/credentialshost /run/kautuka/credentialshost' \
      '' \
      '[Install]' \
      'WantedBy=multi-user.target' | sudo tee "$rootfs_dir/etc/systemd/system/kautuka-credentialshost.service" >/dev/null
    sudo ln -sfn ../kautuka-credentialshost.service "$rootfs_dir/etc/systemd/system/multi-user.target.wants/kautuka-credentialshost.service"
  fi
  install_system_channel_service channelhost ChannelHost kautuka-channelhost "$manager_extract_dir/services/channelhost" kautuka-channelhost
  install_system_channel_service gateway Gateway kautuka-gateway "$manager_extract_dir/services/gateway" kautuka-gateway
  if awk -F: '$1 == "kautuka-gateway" { found=1 } END { exit !found }' "$rootfs_dir/etc/passwd"; then
    sudo chroot "$rootfs_dir" usermod --append --groups kautuka kautuka-gateway
    if awk -F: '$1 == "kautuka-credentials-client" { found=1 } END { exit !found }' "$rootfs_dir/etc/group"; then
      sudo chroot "$rootfs_dir" usermod --append --groups kautuka-credentials-client kautuka-gateway
    fi
  fi
  install_socket_runtime_dirs

  install_host_service updateservice kautuka-updateservice "$manager_extract_dir/services/updateservice"
  [[ -d "$manager_extract_dir/services/updateservice" ]] && installed_units+=(kautuka-updateservice.service)
  install_host_service webhost kautuka-webhost "$manager_extract_dir/services/webhost"
  [[ -d "$manager_extract_dir/services/webhost" ]] && installed_units+=(kautuka-webhost.service)

  activation_target_units="$(printf '%s\n' "${installed_units[@]}" | paste -sd' ' -)"
  printf '%s\n' \
    '[Unit]' \
    'Description=Kautuka appliance runtime' \
    "Wants=${activation_target_units}" \
    'After=network.target' \
    '' \
    '[Install]' \
    'WantedBy=default.target' | sudo tee "$user_unit_dir/kautuka.target" >/dev/null

  sudo ln -sfn ../kautuka.target "$wants_dir/kautuka.target"

  sudo mkdir -p "$rootfs_dir/usr/local/lib/kautuka" "$rootfs_dir/etc/sudoers.d"
  # Prefer the repo helper over any helper embedded in the platform tarball so
  # --prewarm-only rebakes pick up apply-release fixes without republishing.
  if [[ -f "$source_root/scripts/kautuka-apply-release.sh" ]]; then
    sudo install -m 0755 "$source_root/scripts/kautuka-apply-release.sh" "$rootfs_dir/usr/local/lib/kautuka/apply-release"
  elif [[ -f "$manager_extract_dir/bin/kautuka-apply-release" ]]; then
    sudo install -m 0755 "$manager_extract_dir/bin/kautuka-apply-release" "$rootfs_dir/usr/local/lib/kautuka/apply-release"
  fi
  printf '%s\n' 'kautuka ALL=(root) NOPASSWD: /usr/local/lib/kautuka/apply-release' \
    | sudo tee "$rootfs_dir/etc/sudoers.d/kautuka-apply-release" >/dev/null
  sudo chmod 0440 "$rootfs_dir/etc/sudoers.d/kautuka-apply-release"

  channelhost_bin="$manager_extract_dir/services/channelhost/kautuka-channelhost"
  channel_security_root="$rootfs_dir/var/lib/kautuka/security/channel"
  if [[ ! -x "$channelhost_bin" ]]; then
    echo "error: channelhost binary missing or not executable: $channelhost_bin" >&2
    exit 1
  fi
  if ! sudo test -e "$channel_security_root/gateway-private"; then
    sudo mkdir -p "$rootfs_dir/var/lib/kautuka/security" "$channel_security_root"
    sudo "$channelhost_bin" --provision-channel-security \
      "$channel_security_root/gateway-private" \
      "$channel_security_root/gateway-trust" \
      "$channel_security_root/channel-private" \
      "$channel_security_root/channel-trust" \
      "$channel_security_root/offline-authority"
  fi
  if ! sudo test -d "$channel_security_root/channel-private" \
    || ! sudo test -f "$channel_security_root/channel-private/channel-server.pfx"; then
    echo "error: channel security material missing at $channel_security_root after provision." >&2
    sudo find "$rootfs_dir/var/lib/kautuka/security" -printf '%p %u:%g %m\n' 2>/dev/null || true
    exit 1
  fi

  # Installer parity: never fold channel security material into kautuka ownership.
  chown_tree_excluding "$visible_root" "$kautuka_uid:$kautuka_gid" "$channel_security_root"
  if [[ "$install_root" != "$visible_root" ]]; then
    sudo chown -R "$kautuka_uid:$kautuka_gid" "$install_root"
  fi
  sudo chown -R "$kautuka_uid:$kautuka_gid" "$rootfs_dir/opt/kautuka/updateservice" "$rootfs_dir/opt/kautuka/webhost" 2>/dev/null || true
  sudo chown -R 0:0 "$rootfs_dir/opt/kautuka/gateway" "$rootfs_dir/opt/kautuka/channelhost" "$rootfs_dir/opt/kautuka/credentialshost" 2>/dev/null || true

  credential_uid="$(rootfs_uid kautuka-credentials)"
  credential_client_gid="$(rootfs_gid kautuka-credentials-client)"
  if [[ -n "$credential_uid" && -n "$credential_client_gid" ]]; then
    sudo mkdir -p "$rootfs_dir/var/lib/kautuka/credentialsHost/vault" "$rootfs_dir/var/lib/kautuka/credentialsHost/transport"
    sudo chown -R "$credential_uid:$credential_uid" \
      "$rootfs_dir/var/lib/kautuka/credentials/vault" \
      "$rootfs_dir/var/lib/kautuka/credentialsHost/vault" \
      "$rootfs_dir/var/lib/kautuka/logs/credentialshost"
    sudo chown "$credential_uid:$credential_client_gid" \
      "$rootfs_dir/var/lib/kautuka/credentials" \
      "$rootfs_dir/var/lib/kautuka/credentials/transport" \
      "$rootfs_dir/var/lib/kautuka/credentialsHost" \
      "$rootfs_dir/var/lib/kautuka/credentialsHost/transport"
    sudo chmod 700 \
      "$rootfs_dir/var/lib/kautuka/credentials/vault" \
      "$rootfs_dir/var/lib/kautuka/credentialsHost/vault" \
      "$rootfs_dir/var/lib/kautuka/logs/credentialshost"
    sudo chmod 2750 \
      "$rootfs_dir/var/lib/kautuka/credentials" \
      "$rootfs_dir/var/lib/kautuka/credentials/transport" \
      "$rootfs_dir/var/lib/kautuka/credentialsHost" \
      "$rootfs_dir/var/lib/kautuka/credentialsHost/transport"
  fi

  gateway_uid="$(rootfs_uid kautuka-gateway)"
  gateway_gid="$(rootfs_gid kautuka-gateway)"
  channelhost_uid="$(rootfs_uid kautuka-channelhost)"
  channelhost_gid="$(rootfs_gid kautuka-channelhost)"
  if [[ -z "$gateway_uid" || -z "$gateway_gid" || -z "$channelhost_uid" || -z "$channelhost_gid" ]]; then
    echo 'error: failed to resolve gateway/channelhost uid/gid while applying channel security ownership.' >&2
    exit 1
  fi

  sudo mkdir -p "$rootfs_dir/var/lib/kautuka/logs/gateway"
  sudo chown -R "$gateway_uid:$gateway_gid" "$rootfs_dir/var/lib/kautuka/logs/gateway"
  sudo chmod 700 "$rootfs_dir/var/lib/kautuka/logs/gateway"
  sudo mkdir -p "$rootfs_dir/var/lib/kautuka/mcpb-bundles"
  sudo chown -R "$gateway_uid:$kautuka_gid" "$rootfs_dir/var/lib/kautuka/mcpb-bundles"
  sudo chmod 2750 "$rootfs_dir/var/lib/kautuka/mcpb-bundles"
  sudo mkdir -p "$rootfs_dir/var/lib/kautuka/channelHost" "$rootfs_dir/var/lib/kautuka/logs/channelhost"
  sudo chown -R "$channelhost_uid:$channelhost_gid" "$rootfs_dir/var/lib/kautuka/channelHost" "$rootfs_dir/var/lib/kautuka/logs/channelhost"
  sudo chmod 700 "$rootfs_dir/var/lib/kautuka/channelHost" "$rootfs_dir/var/lib/kautuka/logs/channelhost"

  sudo install -D -m 0644 "$manager_extract_dir/LICENSE" "$rootfs_dir/usr/share/licenses/kautuka/LICENSE"
  sudo install -D -m 0644 "$manager_extract_dir/NOTICE" "$rootfs_dir/usr/share/licenses/kautuka/NOTICE"

  # Match installer apply_channel_security_material_permissions.
  sudo chown 0:0 "$rootfs_dir/var/lib/kautuka/security" "$channel_security_root"
  sudo chmod 0711 "$rootfs_dir/var/lib/kautuka/security" "$channel_security_root"
  sudo chown -R "$gateway_uid:$gateway_gid" "$channel_security_root/gateway-private"
  sudo chmod 0700 "$channel_security_root/gateway-private"
  sudo find "$channel_security_root/gateway-private" -type f -exec chmod 0600 {} +
  sudo chown -R "0:$gateway_gid" "$channel_security_root/gateway-trust"
  sudo chmod 0750 "$channel_security_root/gateway-trust"
  sudo find "$channel_security_root/gateway-trust" -type f -exec chmod 0440 {} +
  sudo chown -R "$channelhost_uid:$channelhost_gid" "$channel_security_root/channel-private"
  sudo chmod 0700 "$channel_security_root/channel-private"
  sudo find "$channel_security_root/channel-private" -type f -exec chmod 0600 {} +
  sudo chown -R "0:$channelhost_gid" "$channel_security_root/channel-trust"
  sudo chmod 0750 "$channel_security_root/channel-trust"
  sudo find "$channel_security_root/channel-trust" -type f -exec chmod 0440 {} +
  sudo chown -R 0:0 "$channel_security_root/offline-authority"
  sudo chmod 0700 "$channel_security_root/offline-authority"
  sudo find "$channel_security_root/offline-authority" -type f -exec chmod 0600 {} +

  require_rootfs_owner_mode "$channel_security_root/channel-private" "${channelhost_uid}:${channelhost_gid}:700"
  require_rootfs_owner_mode "$channel_security_root/channel-private/channel-server.pfx" "${channelhost_uid}:${channelhost_gid}:600"
  require_rootfs_owner_mode "$channel_security_root/channel-trust" "0:${channelhost_gid}:750"
  require_rootfs_owner_mode "$channel_security_root/gateway-private" "${gateway_uid}:${gateway_gid}:700"
  require_rootfs_owner_mode "$channel_security_root/gateway-trust" "0:${gateway_gid}:750"
  require_rootfs_owner_mode "$channel_security_root/offline-authority" "0:0:700"

  # Match installer prepare_isolated_service_ancestors: isolated units must
  # traverse the shared state root, and Gateway must be able to create kautuka.db.
  if [[ -n "$kautuka_gid" ]]; then
    sudo chown "0:$kautuka_gid" "$rootfs_dir/var/lib/kautuka" "$rootfs_dir/var/lib/kautuka/logs"
    sudo chmod 0771 "$rootfs_dir/var/lib/kautuka" "$rootfs_dir/var/lib/kautuka/logs"
  fi

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
  local packed_channel_private_owner
  packed_channel_private_owner="$(tar -tvf "$rootfs_output" | awk '/\.\/var\/lib\/kautuka\/security\/channel\/channel-private\/channel-server\.pfx$/ { owner = $2 } END { print owner }')"
  [[ "$packed_channel_private_owner" == "${channelhost_uid}/${channelhost_gid}" ]] || {
    echo "error: appliance channel-private owner must be ${channelhost_uid}/${channelhost_gid} (found ${packed_channel_private_owner:-missing})" >&2
    exit 1
  }

  assert_packed_appliance_network_seal "$rootfs_output" "$kautuka_uid"
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
  --runtime-images "$source_root/src/backend/Kautuka.Manager/runtime-images.json" \
  "${manifest_args[@]}"

(
  cd "$release_dir"
  sha256sum kautuka-linux-amd64.tar.gz kautuka-wsl-appliance.tar.gz kautuka-platform-release.json kautuka-platform-release.sig > kautuka-release-assets.sha256
)

printf 'Release artifacts written to %s\n' "$release_dir"
printf 'Manifest: %s\n' "$release_dir/kautuka-platform-release.json"
