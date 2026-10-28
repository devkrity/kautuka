#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/stage-third-party-sources.sh --release-dir <dir> --wsl-rootfs <extracted-rootfs> --base-image <image@sha256:digest>

Stages immutable source snapshots for distributed sidecar images and the
elkjs frontend component, plus source RPMs for Fedora packages installed in
the WSL rootfs and kautuka-base.
EOF
}

release_dir=''
wsl_rootfs=''
base_image=''
runtime="${KAUTUKA_RELEASE_CONTAINER_RUNTIME:-docker}"
curl_bin="${KAUTUKA_RELEASE_CURL:-curl}"
fedora_image="${FEDORA_SOURCE_IMAGE:-quay.io/fedora/fedora:44}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --release-dir) release_dir="$2"; shift 2 ;;
    --wsl-rootfs) wsl_rootfs="$2"; shift 2 ;;
    --base-image) base_image="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown argument: $1" >&2; usage >&2; exit 1 ;;
  esac
done
if [[ -z "$release_dir" || -z "$wsl_rootfs" || -z "$base_image" ]]; then
  usage >&2
  exit 2
fi
if [[ ! "$base_image" =~ ^ghcr\.io/devkrity/kautuka-base@sha256:[a-f0-9]{64}$ ]]; then
  echo 'error: base image must be the exact immutable kautuka-base digest for this release.' >&2
  exit 2
fi
if [[ ! -f "$wsl_rootfs/usr/share/doc/kautuka/FEDORA_SOURCE_RPMS.txt" ]]; then
  echo 'error: WSL rootfs source RPM manifest is missing; rebuild it with the release source manifest enabled.' >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
release_dir="$(mkdir -p "$release_dir" && cd "$release_dir" && pwd)"
source_dir="$release_dir/sources"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
mkdir -p "$source_dir" "$work_dir/srpms"

fetch_source() {
  local id="$1"
  local repository="$2"
  local commit="$3"
  local archive="$source_dir/$id-$commit.tar.gz"
  "$curl_bin" --fail --location --silent --show-error \
    "https://codeload.github.com/$repository/tar.gz/$commit" \
    --output "$archive"
  test -s "$archive"
}

while IFS=$'\t' read -r id repository commit; do
  fetch_source "$id" "$repository" "$commit"
done < <(node - "$repo_root/compliance/third-party-components.json" <<'NODE'
const fs = require('node:fs');
const catalog = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
for (const id of ['openobserve', 'git-pkgs-proxy', 'elkjs', 'elkjs-mermaid']) {
  const component = catalog.components.find((item) => item.id === id);
  if (!component?.sourceCommit || !component.source) throw new Error(`Missing exact source evidence for ${id}`);
  const url = new URL(component.source);
  const repository = url.pathname.split('/').filter(Boolean).slice(0, 2).join('/');
  if (url.hostname !== 'github.com' || repository.split('/').length !== 2) throw new Error(`Unsupported source URL for ${id}`);
  process.stdout.write(`${id}\t${repository}\t${component.sourceCommit}\n`);
}
NODE
)

base_sources="$("$runtime" run --rm --entrypoint /bin/cat "$base_image" /usr/share/doc/kautuka/FEDORA_SOURCE_RPMS.txt)"
{
  cat "$wsl_rootfs/usr/share/doc/kautuka/FEDORA_SOURCE_RPMS.txt"
  printf '%s\n' "$base_sources"
} | sed '/^[[:space:]]*$/d' | sort -u > "$work_dir/fedora-srpm-list.txt"
test -s "$work_dir/fedora-srpm-list.txt"

runtime_args=()
if [[ "$runtime" == 'podman' ]]; then
  runtime_args+=(--pull=always)
fi
"$runtime" run --rm "${runtime_args[@]}" \
  -v "$work_dir/fedora-srpm-list.txt:/input/fedora-srpm-list.txt:ro" \
  -v "$work_dir/srpms:/output/srpms:rw" \
  --entrypoint /bin/bash "$fedora_image" -euc '
    dnf install -y dnf-plugins-core
    mapfile -t source_specs < <(sed "s/\.src\.rpm$//" /input/fedora-srpm-list.txt)
    if (( ${#source_specs[@]} == 0 )); then
      echo "error: the Fedora package manifests had no source RPM entries." >&2
      exit 1
    fi
    dnf download --source --destdir /output/srpms "${source_specs[@]}"
    while IFS= read -r srpm; do
      if [[ ! -s "/output/srpms/$srpm" ]]; then
        echo "error: source RPM was not retrieved: $srpm" >&2
        exit 1
      fi
    done < /input/fedora-srpm-list.txt
  '

cp "$work_dir/fedora-srpm-list.txt" "$source_dir/Fedora-SOURCE-RPMS.txt"
(cd "$work_dir/srpms" && sha256sum *.src.rpm) > "$source_dir/Fedora-SOURCE-RPMS.sha256"
tar -czf "$source_dir/Fedora-SOURCE-RPMS.tar.gz" -C "$work_dir/srpms" .

cat > "$source_dir/SOURCE_ARCHIVES.md" <<'EOF'
# Release source archives

These archives accompany this release's distributed binaries and container
images. The Git snapshots are pinned to the exact source commits recorded in
`../THIRD_PARTY_COMPONENTS.json`. The Fedora archive contains the source RPMs
for the complete installed package sets in the release's WSL rootfs and exact
`kautuka-base` image digest. `Fedora-SOURCE-RPMS.txt` names each source RPM and
`Fedora-SOURCE-RPMS.sha256` verifies the source RPM files within the archive.

The OpenObserve and git-pkgs/proxy snapshots are complete source repository
archives from the commits used for the release's pinned OCI images. The elkjs
snapshots cover both the direct frontend 0.12.0 dependency and the transitive
0.9.3 Mermaid dependency. Their EPL-2.0 license records require source
availability and reasonable retrieval directions for distributed programs;
see the adjacent Kautuka third-party notices and component inventory.
EOF
(cd "$source_dir" && sha256sum openobserve-*.tar.gz git-pkgs-proxy-*.tar.gz elkjs-*.tar.gz Fedora-SOURCE-RPMS.tar.gz Fedora-SOURCE-RPMS.txt Fedora-SOURCE-RPMS.sha256 SOURCE_ARCHIVES.md > SHA256SUMS)
printf 'Staged source archives in %s\n' "$source_dir"
