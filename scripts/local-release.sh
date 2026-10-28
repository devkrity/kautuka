#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/local-release.sh [options]

Builds the current branch into a local signed release feed under artifacts/local-release-publish,
updates the stable local feed directory, and optionally serves it over HTTP.
On non-Windows hosts, the local bundle skips Kautuka.Installer.exe because cross-OS native publish is unsupported.

Options:
  --release-id <id>               Optional metadata-only release id. Default: <release-tag>.
  --output-root <dir>               Output root. Default: artifacts/local-release-publish
  --private-key <pem>              Manifest signing private key PEM.
  --public-key <pem>               Manifest signing public key PEM.
  --release-tag <tag>              Override release tag. Default: v<derived-version>.
  --version <version>              Manifest/build version. Local default is <KautukaDevVersion>-dev.
  --channel <channel>              Release channel: preview or stable. Default: preview.
  --minimum-updater-version <ver>  Optional minimum updater version.
  --listen-host <host>             HTTP bind host. Default: 0.0.0.0.
  --public-base-url <url>          Base URL embedded into local installer assets.
  --port <port>                    Local HTTP port when serving. Default: 20300.
  --build-only                     Build the local release feed but do not start the HTTP server.
  --serve-only                     Serve the existing local release feed without rebuilding it.
  --rebuild-images                 Rebuild the cached WSL base rootfs and Linux release build image.
  --selinux-smoke                  Run the SELinux sandbox smoke check after building the release feed.
  -h, --help                       Show this help.
EOF
}

output_root=''
private_key=''
public_key=''
release_id=''
release_tag=''
version=''
channel='preview'
minimum_updater_version=''
listen_host='0.0.0.0'
public_base_url=''
port='20300'
build_only='false'
serve_only='false'
rebuild_images='false'
selinux_smoke='false'
catalog_connector_artifact_file=''
catalog_connector_artifact_path=''
catalog_skill_artifact_file=''
catalog_skill_artifact_path=''

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
    --public-key)
      public_key="$2"
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
    --channel)
      channel="$2"
      shift 2
      ;;
    --minimum-updater-version)
      minimum_updater_version="$2"
      shift 2
      ;;
    --listen-host)
      listen_host="$2"
      shift 2
      ;;
    --public-base-url)
      public_base_url="$2"
      shift 2
      ;;
    --port)
      port="$2"
      shift 2
      ;;
    --build-only)
      build_only='true'
      shift
      ;;
    --serve-only)
      serve_only='true'
      shift
      ;;
    --rebuild-images)
      rebuild_images='true'
      shift
      ;;
    --selinux-smoke)
      selinux_smoke='true'
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

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"

read_dev_version() {
  local props_file="$repo_root/src/Directory.Build.props"
  local parsed=''

  if [[ -f "$props_file" ]]; then
    parsed="$(sed -n 's:.*<KautukaDevVersion>\(.*\)</KautukaDevVersion>.*:\1:p' "$props_file" | head -n 1 | tr -d '[:space:]')"
  fi

  if [[ -n "$parsed" ]]; then
    printf '%s\n' "$parsed"
    return
  fi

  printf '0.1.20-dev\n'
}

output_root="${output_root:-$repo_root/artifacts/local-release-publish}"
releases_root="$output_root/releases"
feed_dir="$output_root/current"
keys_dir="$output_root/keys"
private_key="${private_key:-$keys_dir/manifest-signing-private.pem}"
public_key="${public_key:-$keys_dir/manifest-signing-public.pem}"
bundle_root="$output_root/bundles"

mkdir -p "$releases_root" "$keys_dir" "$bundle_root"

if [[ "$build_only" == 'true' && "$serve_only" == 'true' ]]; then
  echo 'error: --build-only and --serve-only cannot be used together.' >&2
  exit 1
fi

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "error: required command not found: $1" >&2
    exit 1
  fi
}

port_conflict_details() {
  local requested_port="$1"

  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"$requested_port" -sTCP:LISTEN 2>/dev/null | tail -n +2 | head -n 3
    return 0
  fi

  if command -v ss >/dev/null 2>&1; then
    ss -ltnp "sport = :$requested_port" 2>/dev/null | tail -n +2 | head -n 3
    return 0
  fi

  if command -v fuser >/dev/null 2>&1; then
    fuser -n tcp "$requested_port" 2>/dev/null | head -n 1
  fi
}

is_port_in_use() {
  local requested_port="$1"

  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"$requested_port" -sTCP:LISTEN >/dev/null 2>&1
    return $?
  fi

  if command -v ss >/dev/null 2>&1; then
    ss -ltn "sport = :$requested_port" 2>/dev/null | tail -n +2 | grep -q .
    return $?
  fi

  if command -v fuser >/dev/null 2>&1; then
    fuser -n tcp "$requested_port" >/dev/null 2>&1
    return $?
  fi

  python3 - "$requested_port" <<'EOF'
import socket
import sys

port = int(sys.argv[1])
with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
    try:
        sock.bind(("0.0.0.0", port))
    except OSError:
        sys.exit(0)
sys.exit(1)
EOF
}

ensure_port_available() {
  local requested_port="$1"

  if ! is_port_in_use "$requested_port"; then
    return 0
  fi

  echo "error: local release feed port $requested_port is already in use." >&2
  echo 'hint: stop the existing local feed server, or rerun with --port <free-port> or --build-only.' >&2

  local details=''
  details="$(port_conflict_details "$requested_port" || true)"
  if [[ -n "$details" ]]; then
    printf 'listener:\n%s\n' "$details" >&2
  fi

  exit 1
}

ensure_supported_invocation() {
  if [[ ${EUID} -eq 0 && -n ${SUDO_USER:-} ]]; then
    echo "error: do not run scripts/local-release.sh under sudo." >&2
    echo "hint: run scripts/local-release.sh as ${SUDO_USER}; it will prompt for sudo only when the rootfs step needs it." >&2
    exit 1
  fi
}

ensure_rootfs_build_prerequisites() {
  if [[ ${EUID} -eq 0 ]]; then
    return 0
  fi

  if ! command -v sudo >/dev/null 2>&1; then
    echo "error: local release build requires sudo access to create the WSL rootfs, but sudo is not available on this host." >&2
    exit 1
  fi
}

ensure_existing_feed() {
  local existing_feed_dir="$1"

  if [[ ! -d "$existing_feed_dir" ]]; then
    echo "error: local release feed directory does not exist: $existing_feed_dir" >&2
    echo 'hint: run scripts/local-release.sh once without --serve-only, or point --output-root at an existing local release publish directory.' >&2
    exit 1
  fi

  if [[ ! -f "$existing_feed_dir/kautuka-platform-release.json" ]]; then
    echo "error: local release feed is incomplete; manifest not found: $existing_feed_dir/kautuka-platform-release.json" >&2
    echo 'hint: run scripts/local-release.sh once without --serve-only to build the local feed first.' >&2
    exit 1
  fi
}

is_windows_host() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

ensure_signing_keys() {
  require_command openssl

  if [[ ! -f "$private_key" ]]; then
    echo "Creating local manifest signing keypair in $keys_dir"
    openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$private_key"
    chmod 600 "$private_key"
  fi

  if [[ ! -f "$public_key" || "$private_key" -nt "$public_key" ]]; then
    openssl pkey -in "$private_key" -pubout -out "$public_key"
    chmod 644 "$public_key"
  fi
}

sanitize_token() {
  printf '%s' "$1" | tr -cs '[:alnum:]._-' '-'
}

detect_primary_ip() {
  local host_ip=''
  if command -v hostname >/dev/null 2>&1; then
    host_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  fi

  if [[ -z "$host_ip" ]] && command -v ip >/dev/null 2>&1; then
    host_ip="$(ip route get 1.1.1.1 2>/dev/null | awk '/src/ { for (i = 1; i <= NF; i += 1) if ($i == "src") { print $(i + 1); exit } }')"
  fi

  printf '%s' "${host_ip:-127.0.0.1}"
}

read_manifest_version() {
  local manifest_path="$1"
  sed -n 's|.*"version": "\([^"]*\)".*|\1|p' "$manifest_path" | head -n 1
}

read_manifest_channel() {
  local manifest_path="$1"
  sed -n 's|.*"channel": "\([^"]*\)".*|\1|p' "$manifest_path" | head -n 1
}

read_manifest_published_at() {
  local manifest_path="$1"
  sed -n 's|.*"publishedAt": "\([^"]*\)".*|\1|p' "$manifest_path" | head -n 1
}

read_wrapper_version() {
  local wrapper_path="$1"
  sed -n 's:^KAUTUKA_DEFAULT_VERSION=\(.*\)$:\1:p' "$wrapper_path" | head -n 1
}

sync_current_feed() {
  local source_release_dir="$1"
  local current_feed_dir="$2"
  local manifest_public_key_path="$3"
  local loopback_feed_base_url="$4"
  local expected_version="$5"
  local staging_feed_dir="$current_feed_dir.tmp"

  rm -rf "$staging_feed_dir"
  mkdir -p "$staging_feed_dir"
  cp -a "$source_release_dir/." "$staging_feed_dir/"
  cp "$manifest_public_key_path" "$staging_feed_dir/manifest-public-key.pem"

  cat > "$staging_feed_dir/local-release.env" <<EOF
KAUTUKA_SMOKE_RELEASE_DIR=$current_feed_dir
KAUTUKA_SMOKE_RELEASE_BASE_URL=$loopback_feed_base_url
KAUTUKA_SMOKE_MANIFEST_PUBLIC_KEY_PATH=$current_feed_dir/manifest-public-key.pem
EOF

  local staged_manifest_version=''
  local staged_wrapper_version=''
  staged_manifest_version="$(read_manifest_version "$staging_feed_dir/kautuka-platform-release.json")"
  staged_wrapper_version="$(read_wrapper_version "$staging_feed_dir/kautuka-install-linux.sh")"

  if [[ "$staged_manifest_version" != "$expected_version" ]]; then
    echo "error: staged current feed manifest version '$staged_manifest_version' did not match expected '$expected_version'." >&2
    exit 1
  fi

  if [[ "$staged_wrapper_version" != "$expected_version" ]]; then
    echo "error: staged current feed installer version '$staged_wrapper_version' did not match expected '$expected_version'." >&2
    exit 1
  fi

  rm -rf "$current_feed_dir"
  mv "$staging_feed_dir" "$current_feed_dir"
}

update_channel_latest_link() {
  local output_dir="$1"
  local release_dir="$2"
  local release_channel="$3"

  case "$release_channel" in
    preview)
      ln -sfn "$release_dir" "$output_dir/latest-preview"
      ;;
    stable)
      ln -sfn "$release_dir" "$output_dir/latest-stable"
      ;;
  esac
}

prune_local_release_history() {
  local releases_dir="$1"
  local bundles_dir="$2"
  local output_dir="$3"

  local latest_preview_id=''
  local latest_preview_published_at=''
  local latest_stable_id=''
  local latest_stable_published_at=''
  local release_path=''

  shopt -s nullglob

  for release_path in "$releases_dir"/*; do
    [[ -d "$release_path" ]] || continue

    local release_name
    local manifest_path
    local release_channel
    local published_at
    release_name="$(basename "$release_path")"
    manifest_path="$release_path/kautuka-platform-release.json"

    if [[ "$release_name" == .build-release-assets.* || "$release_name" == .build-release-smoke.* ]]; then
      rm -rf "$release_path"
      continue
    fi

    [[ -f "$manifest_path" ]] || continue

    release_channel="$(read_manifest_channel "$manifest_path")"
    published_at="$(read_manifest_published_at "$manifest_path")"

    case "$release_channel" in
      preview)
        if [[ -z "$latest_preview_published_at" || "$published_at" > "$latest_preview_published_at" ]]; then
          latest_preview_published_at="$published_at"
          latest_preview_id="$release_name"
        fi
        ;;
      stable)
        if [[ -z "$latest_stable_published_at" || "$published_at" > "$latest_stable_published_at" ]]; then
          latest_stable_published_at="$published_at"
          latest_stable_id="$release_name"
        fi
        ;;
    esac
  done

  if [[ -n "$latest_preview_id" ]]; then
    update_channel_latest_link "$output_dir" "$releases_dir/$latest_preview_id" preview
  else
    rm -f "$output_dir/latest-preview"
  fi

  if [[ -n "$latest_stable_id" ]]; then
    update_channel_latest_link "$output_dir" "$releases_dir/$latest_stable_id" stable
  else
    rm -f "$output_dir/latest-stable"
  fi

  for release_path in "$releases_dir"/*; do
    [[ -d "$release_path" ]] || continue

    local release_name
    release_name="$(basename "$release_path")"

    if [[ "$release_name" == .build-release-assets.* || "$release_name" == .build-release-smoke.* ]]; then
      rm -rf "$release_path"
      continue
    fi

    if [[ "$release_name" == "$latest_preview_id" || "$release_name" == "$latest_stable_id" ]]; then
      continue
    fi

    rm -rf "$release_path"
    rm -rf "$bundles_dir/$release_name"
  done

  shopt -u nullglob
}

write_release_asset_manifest() {
  local target_dir="$1"
  local files=(
    catalog/manifest.json
    catalog/manifest.sig
    kautuka-install-linux.sh
    kautuka-linux-amd64.tar.gz
    kautuka-wsl-appliance.tar.gz
    kautuka-platform-release.json
    kautuka-platform-release.sig
  )

  if [[ -f "$target_dir/Kautuka.Installer.exe" ]]; then
    files=(Kautuka.Installer.exe "${files[@]}")
  fi

  shopt -s nullglob
  local catalog_artifact=''
  for catalog_artifact in "$target_dir"/catalog/artifacts/*; do
    [[ -f "$catalog_artifact" ]] || continue
    files+=("${catalog_artifact#"$target_dir"/}")
  done
  shopt -u nullglob

  (
    cd "$target_dir"
    sha256sum "${files[@]}" > kautuka-release-assets.sha256
  )

  TARGET_DIR="$target_dir" node <<'EOF'
const fs = require('node:fs');
const path = require('node:path');

const targetDir = process.env.TARGET_DIR;
const files = [
  'catalog/manifest.json',
  'catalog/manifest.sig',
  'Kautuka.Installer.exe',
  'kautuka-install-linux.sh',
  'kautuka-linux-amd64.tar.gz',
  'kautuka-wsl-appliance.tar.gz',
  'kautuka-platform-release.json',
  'kautuka-platform-release.sig',
  'kautuka-release-assets.sha256',
].filter((fileName) => fs.existsSync(path.join(targetDir, fileName)));

const catalogArtifactsDir = path.join(targetDir, 'catalog', 'artifacts');
if (fs.existsSync(catalogArtifactsDir)) {
  for (const entry of fs.readdirSync(catalogArtifactsDir).sort()) {
    const relativePath = path.posix.join('catalog', 'artifacts', entry);
    if (fs.statSync(path.join(targetDir, relativePath)).isFile()) {
      files.push(relativePath);
    }
  }
}

const assets = files.map((fileName) => {
  const fullPath = path.join(targetDir, fileName);
  const sizeBytes = fs.statSync(fullPath).size;
  return {
    name: fileName,
    relativePath: fileName,
    sizeBytes,
  };
});

const manifest = {
  schemaVersion: 1,
  assets,
};

fs.writeFileSync(path.join(targetDir, 'kautuka-release-assets.json'), `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
EOF
}

build_catalog_manifest() {
  local release_root="$1"
  local signing_private_key="$2"
  local manifest_base_url="$3"

  local args=(
    node "$repo_root/scripts/generate-catalog-manifest.mjs"
    --output-dir "$release_root/catalog" \
    --private-key "$signing_private_key" \
    --manifest-base-url "$manifest_base_url"
  )

  if [[ -n "$catalog_connector_artifact_file" && -n "$catalog_connector_artifact_path" ]]; then
    args+=(
      --connector-artifact-file "$catalog_connector_artifact_file"
      --connector-artifact-path "$catalog_connector_artifact_path"
    )
  fi

  if [[ -n "$catalog_skill_artifact_file" && -n "$catalog_skill_artifact_path" ]]; then
    args+=(
      --docs-artifact-file "$catalog_skill_artifact_file"
      --docs-artifact-path "$catalog_skill_artifact_path"
    )
  fi

  "${args[@]}"
}

stage_catalog_artifacts() {
  local release_root="$1"
  local catalog_artifacts_dir="$release_root/catalog/artifacts"
  local built_in_mcpb_source_dir="$repo_root/mcps/kautuka-web-tools"
  local built_in_mcpb_builder="$repo_root/scripts/stage-built-in-mcpb.sh"
  local skill_source_dir="$repo_root/skills/kautuka-docs"

  catalog_connector_artifact_file=''
  catalog_connector_artifact_path=''
  catalog_skill_artifact_file=''
  catalog_skill_artifact_path=''

  rm -rf "$catalog_artifacts_dir"
  mkdir -p "$catalog_artifacts_dir"

  if [[ -d "$built_in_mcpb_source_dir" ]]; then
    catalog_connector_artifact_file="$catalog_artifacts_dir/kautuka-web-tools.mcpb"
    "$built_in_mcpb_builder" --output "$catalog_connector_artifact_file"
    catalog_connector_artifact_path="artifacts/$(basename "$catalog_connector_artifact_file")"
  fi

  if [[ -d "$skill_source_dir" ]]; then
    catalog_skill_artifact_file="$catalog_artifacts_dir/kautuka-docs.zip"
    python3 - "$skill_source_dir" "$catalog_skill_artifact_file" <<'EOF'
import pathlib
import sys
import zipfile

source = pathlib.Path(sys.argv[1]).resolve()
archive = pathlib.Path(sys.argv[2]).resolve()
archive.parent.mkdir(parents=True, exist_ok=True)

with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED) as bundle:
    for file_path in sorted(source.rglob('*')):
        if file_path.is_file():
            bundle.write(file_path, file_path.relative_to(source.parent))
EOF
    catalog_skill_artifact_path="artifacts/$(basename "$catalog_skill_artifact_file")"
  fi
}

build_windows_installer() {
  local publish_root="$1"
  local installer_version="$2"
  local manifest_public_key_source="$3"
  local release_manifest_url="$4"

  mkdir -p "$publish_root"

  dotnet publish "$repo_root/src/installer/Kautuka.Installer.Windows/Kautuka.Installer.Windows.csproj" \
    --runtime win-x64 \
    -p:EnableWindowsTargeting=true \
    -p:InstallerVersion="$installer_version" \
    -p:InstallerReleaseTag="$release_tag" \
    -p:InstallerReleaseId="$version" \
    -p:InstallerReleaseManifestUrl="$release_manifest_url" \
    -p:InstallerManifestPublicKeyPath="$manifest_public_key_source" \
    -c Release \
    -o "$publish_root"

  if [[ ! -f "$publish_root/Kautuka.Installer.exe" ]]; then
    local published_exe="$publish_root/Kautuka.Installer.Windows.exe"
    if [[ ! -f "$published_exe" ]]; then
      echo 'error: Kautuka.Installer.Windows.exe was not found. Refusing to rename another executable.' >&2
      exit 1
    fi

    mv "$published_exe" "$publish_root/Kautuka.Installer.exe"
  fi
}

maybe_build_windows_installer() {
  local publish_root="$1"
  local installer_version="$2"
  local release_dir="$3"
  local manifest_public_key_source="$4"
  local release_manifest_url="$5"

  if ! is_windows_host; then
    printf 'Skipping Windows installer publish on %s; Cross-OS native compilation is not supported.\n' "$(uname -s)" >&2
    return 0
  fi

  build_windows_installer "$publish_root" "$installer_version" "$manifest_public_key_source" "$release_manifest_url"
  cp "$publish_root/Kautuka.Installer.exe" "$release_dir/Kautuka.Installer.exe"
}

build_linux_installer() {
  local destination_path="$1"
  local feed_base_url="$2"
  local manifest_url="$3"
  local catalog_manifest_url="$4"
  local manifest_public_key_source="$5"

  bash "$repo_root/scripts/generate-linux-installer.sh" \
    --output "$destination_path" \
    --feed-base-url "$feed_base_url" \
    --manifest-url "$manifest_url" \
    --catalog-manifest-url "$catalog_manifest_url" \
    --version "$version" \
    --release-channel "$channel" \
    --manifest-public-key "$manifest_public_key_source" \
    --local-feed-defaults
}

dev_version="$(read_dev_version)"
product_version="${dev_version%-dev}"
version="${version:-$dev_version}"
release_tag="${release_tag:-v$version}"
release_id="${release_id:-$release_tag}"
release_dir="$releases_root/$version"
bundle_dir="$bundle_root/$version"
loopback_base_url="http://127.0.0.1:$port"
public_base_url="${public_base_url:-http://$(detect_primary_ip):$port}"

if [[ ! "$public_base_url" =~ ^https?:// ]]; then
  echo "error: --public-base-url must be an absolute http(s) URL." >&2
  exit 1
fi

# The asset URLs are signed into the manifest, and ReleaseTrustPolicy accepts only https
# or loopback http. A plain-http LAN address therefore passes discovery and then fails at
# apply with "Platform release signature URL must be https or loopback http", leaving the
# appliance advertising an update it can never install. Loopback is enough for a WSL
# appliance, which shares the host's network namespace, and for Windows via
# localhostForwarding; the LAN address is still correct when installing from another machine.
if [[ "$serve_only" != 'true' && "$public_base_url" =~ ^http:// \
      && ! "$public_base_url" =~ ^http://(127\.0\.0\.1|localhost|\[::1\]) ]]; then
  echo "warning: $public_base_url is plain http on a non-loopback address, so an appliance" >&2
  echo "         will discover this release and then refuse to apply it. Pass" >&2
  echo "         --public-base-url $loopback_base_url to exercise the update path." >&2
fi

ensure_signing_keys
ensure_supported_invocation

# Two builds sharing one output root corrupt each other. The prune below deletes every
# release directory that is not the current channel pointer, which includes one a sibling
# build is still assembling: observed as a run that built its platform tarball, had it
# deleted by the sibling's prune, and then failed with ENOENT in its manifest step.
#
# Only builds take the lock. Serving reads the feed, and a long-lived server holding this
# would block the rebuild-then-reserve loop it exists to support.
if [[ "$serve_only" != 'true' ]]; then
  require_command flock
  exec 9>"$output_root/.local-release.lock"
  if ! flock --exclusive --nonblock 9; then
    echo "error: another local-release.sh build is already using $output_root." >&2
    echo "       Wait for it to finish, or pass a different --output-root." >&2
    exit 1
  fi
fi

if [[ "$build_only" != 'true' ]]; then
  require_command python3
  ensure_port_available "$port"
fi

if [[ "$serve_only" == 'true' ]]; then
  ensure_existing_feed "$feed_dir"
  feed_version="$(read_manifest_version "$feed_dir/kautuka-platform-release.json")"
  printf 'Serve-only mode enabled.\n'
  printf 'Served feed directory: %s\n' "$feed_dir"
  printf 'Manifest path: %s\n' "$feed_dir/kautuka-platform-release.json"
  printf 'Release version: %s\n' "$feed_version"
  printf 'Loopback feed endpoint: %s/manifest\n' "$loopback_base_url"
  printf 'Public feed endpoint: %s/manifest\n' "$public_base_url"
  printf 'Loopback catalog endpoint: %s/catalog/manifest\n' "$loopback_base_url"
  printf 'Public catalog endpoint: %s/catalog/manifest\n' "$public_base_url"
  printf 'Directory listing endpoint: %s/\n' "$public_base_url"
  printf 'Serving local release feed on %s:%s\n' "$listen_host" "$port"
  exec node "$repo_root/scripts/serve-release-smoke.mjs" --dir "$feed_dir" --host "$listen_host" --port "$port"
fi

ensure_rootfs_build_prerequisites

build_args=(
  --output-root "$releases_root"
  --private-key "$private_key"
  --release-id "$release_id"
  --release-tag "$release_tag"
  --version "$version"
  --product-version "$product_version"
  --source-sha "$(git -C "$repo_root" rev-parse HEAD)"
  --channel "$channel"
  --manifest-url "$public_base_url/manifest"
  --catalog-manifest-url "$public_base_url/catalog/manifest"
  --artifact-base-url "$public_base_url"
  --manifest-public-key "$public_key"
)

if [[ -n "$minimum_updater_version" ]]; then
  build_args+=(--minimum-updater-version "$minimum_updater_version")
fi

if [[ "$rebuild_images" == 'true' ]]; then
  build_args+=(--rebuild-images)
fi

bash "$repo_root/scripts/build-release-assets.sh" "${build_args[@]}"

installer_version="$product_version"

windows_publish_root="$output_root/windows-installer-build"
rm -rf "$windows_publish_root" "$bundle_dir"
mkdir -p "$bundle_dir"

maybe_build_windows_installer "$windows_publish_root" "$installer_version" "$release_dir" "$public_key" "$public_base_url/manifest"

build_linux_installer \
  "$release_dir/kautuka-install-linux.sh" \
  "$public_base_url" \
  "$public_base_url/manifest" \
  "$public_base_url/catalog/manifest" \
  "$public_key"

stage_catalog_artifacts "$release_dir"

build_catalog_manifest \
  "$release_dir" \
  "$private_key" \
  "$public_base_url/catalog"

write_release_asset_manifest "$release_dir"

cp -a "$release_dir/." "$bundle_dir/"
cp "$public_key" "$bundle_dir/manifest-public-key.pem"

sync_current_feed "$release_dir" "$feed_dir" "$public_key" "$loopback_base_url" "$version"
ln -sfn "$release_dir" "$output_root/latest"
update_channel_latest_link "$output_root" "$release_dir" "$channel"
prune_local_release_history "$releases_root" "$bundle_root" "$output_root"

if [[ "$selinux_smoke" == 'true' ]]; then
  bash "$repo_root/scripts/selinux-smoke.sh" --require-enforcing
fi

printf 'Local release build complete.\n'
printf 'Release directory: %s\n' "$release_dir"
printf 'Served feed directory: %s\n' "$feed_dir"
printf 'LAN testing bundle: %s\n' "$bundle_dir"
printf 'Manifest path: %s\n' "$feed_dir/kautuka-platform-release.json"
printf 'Release version: %s\n' "$version"
printf 'Manifest public key: %s\n' "$feed_dir/manifest-public-key.pem"
if [[ -f "$feed_dir/Kautuka.Installer.exe" ]]; then
  printf 'Windows installer: %s\n' "$feed_dir/Kautuka.Installer.exe"
  printf 'Windows installer URL: %s/Kautuka.Installer.exe\n' "$public_base_url"
else
  printf 'Windows installer: skipped on this host; run the script on Windows to include Kautuka.Installer.exe\n'
fi
printf 'Linux installer: %s\n' "$feed_dir/kautuka-install-linux.sh"
printf 'Linux installer URL: %s/kautuka-install-linux.sh\n' "$public_base_url"
printf 'Loopback feed endpoint: %s/manifest\n' "$loopback_base_url"
printf 'Public feed endpoint: %s/manifest\n' "$public_base_url"
printf 'Loopback catalog endpoint: %s/catalog/manifest\n' "$loopback_base_url"
printf 'Public catalog endpoint: %s/catalog/manifest\n' "$public_base_url"
printf 'Latest preview pointer: %s\n' "$output_root/latest-preview"
printf 'Latest stable pointer: %s\n' "$output_root/latest-stable"
printf 'Directory listing endpoint: %s/\n' "$public_base_url"
printf 'Suggested env file: %s\n' "$feed_dir/local-release.env"

if [[ "$build_only" == 'true' ]]; then
  printf 'Build-only mode enabled.\n'
  exit 0
fi

printf 'Serving local release feed on %s:%s\n' "$listen_host" "$port"
exec node "$repo_root/scripts/serve-release-smoke.mjs" --dir "$feed_dir" --host "$listen_host" --port "$port"
