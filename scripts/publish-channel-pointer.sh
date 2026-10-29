#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  scripts/publish-channel-pointer.sh preflight --channel <stable|preview> --source-sha <sha>
  scripts/publish-channel-pointer.sh publish --channel <stable|preview> --release-tag <tag> --source-sha <sha> --manifest <file>

Required environment:
  RELEASE_FEED_DEPLOY_KEY  The channel environment's write-enabled SSH deploy key.

The script pins GitHub's public SSH host key, requires strict host checking,
keeps the private key in a mode-0600 temporary file, and removes it on exit.
EOF
}

fail() {
  printf 'error: %s\n' "$1" >&2
  exit 1
}

mode="${1:-}"
case "$mode" in
  preflight|publish) shift ;;
  -h|--help) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac

channel=''
source_sha=''
release_tag=''
manifest_path=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --channel) [[ $# -ge 2 ]] || fail 'missing value for --channel'; channel="$2"; shift 2 ;;
    --source-sha) [[ $# -ge 2 ]] || fail 'missing value for --source-sha'; source_sha="$2"; shift 2 ;;
    --release-tag) [[ $# -ge 2 ]] || fail 'missing value for --release-tag'; release_tag="$2"; shift 2 ;;
    --manifest) [[ $# -ge 2 ]] || fail 'missing value for --manifest'; manifest_path="$2"; shift 2 ;;
    *) fail "unknown argument: $1" ;;
  esac
done

case "$channel" in
  stable|preview) ;;
  *) fail 'channel must be stable or preview' ;;
esac
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || fail 'source SHA must be a 40-character lowercase Git SHA'
if [[ "$mode" == 'publish' ]]; then
  if [[ "$channel" == 'stable' ]]; then
    [[ "$release_tag" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail 'stable release tag must be vX.Y.Z'
  else
    [[ "$release_tag" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-preview\.(0|[1-9][0-9]*)$ ]] || fail 'preview release tag must be vX.Y.Z-preview.N'
  fi
  [[ -n "$manifest_path" && -f "$manifest_path" && ! -L "$manifest_path" ]] || fail 'manifest file must be a regular non-symlink file'
  node --input-type=module - "$manifest_path" "$channel" "$release_tag" "$source_sha" <<'NODE'
import { readFile } from 'node:fs/promises';
const [manifestPath, channel, releaseTag, sourceSha] = process.argv.slice(2);
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
if (manifest.channel !== channel || manifest.releaseTag !== releaseTag || manifest.sourceSha !== sourceSha) {
  throw new Error('signed release manifest identity does not match pointer candidate');
}
if (manifest.signature?.algorithm !== 'ecdsa-p256-sha256' || !manifest.signature?.url || !manifest.artifacts?.length) {
  throw new Error('signed release manifest is missing its signature or artifacts');
}
NODE
fi

key_secret="${RELEASE_FEED_DEPLOY_KEY:-}"
[[ -n "$key_secret" ]] || fail "${channel} environment must define its own release_feed_deploy_key"
known_hosts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/github-known-hosts"
[[ -s "$known_hosts" ]] || fail 'pinned GitHub known-hosts file is missing'
command -v ssh-keygen >/dev/null 2>&1 || fail 'ssh-keygen is required'
command -v git >/dev/null 2>&1 || fail 'git is required'

runner_temp="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
mkdir -p "$runner_temp"
umask 077
key_file="$(mktemp "${runner_temp%/}/kautuka-feed-${channel}.XXXXXX")"
work_dir=''
cleanup() {
  [[ -z "$key_file" ]] || rm -f -- "$key_file" 2>/dev/null || true
  [[ -z "$work_dir" ]] || rm -rf -- "$work_dir" 2>/dev/null || true
}
trap cleanup EXIT HUP INT TERM
chmod 600 "$key_file"
printf '%s\n' "$key_secret" > "$key_file"
unset key_secret RELEASE_FEED_DEPLOY_KEY
if ! ssh-keygen -y -P '' -f "$key_file" >/dev/null 2>&1; then
  fail 'release_feed_deploy_key is not a valid unencrypted OpenSSH private key'
fi
[[ "$(stat -c '%a' "$key_file")" == '600' ]] || fail 'temporary deploy key permissions are not 0600'

printf -v key_arg '%q' "$key_file"
printf -v hosts_arg '%q' "$known_hosts"
export GIT_SSH_COMMAND="ssh -i ${key_arg} -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=2 -o StrictHostKeyChecking=yes -o UserKnownHostsFile=${hosts_arg} -o GlobalKnownHostsFile=/dev/null -o UpdateHostKeys=no"
export GIT_CONFIG_NOSYSTEM=1
export GIT_TERMINAL_PROMPT=0
feed_repository="devkrity/kautuka-release-feed-${channel}"
feed_remote="git@github.com:${feed_repository}.git"

if [[ "$mode" == 'preflight' ]]; then
  main_ref="$(git ls-remote --heads "$feed_remote" refs/heads/main)" || fail "SSH read access to ${feed_repository} failed"
  [[ "$main_ref" == *$'\trefs/heads/main' ]] || fail "${feed_repository} must have a main branch"
  run_id="${GITHUB_RUN_ID:-local}"
  attempt="${GITHUB_RUN_ATTEMPT:-1}"
  [[ "$run_id" =~ ^[A-Za-z0-9._-]+$ && "$attempt" =~ ^[A-Za-z0-9._-]+$ ]] || fail 'workflow run identity is malformed'
  preflight_ref="refs/heads/kautuka-release-preflight/${channel}-${run_id}-${attempt}"
  if ! git push --dry-run "$feed_remote" "${source_sha}:${preflight_ref}"; then
    fail "write-permission preflight for ${feed_repository} failed; no pointer or release assets were published"
  fi
  printf 'preflight succeeded for %s using a no-write dry run\n' "$feed_repository"
  exit 0
fi

work_dir="$(mktemp -d "${runner_temp%/}/kautuka-feed-${channel}-publish.XXXXXX")"
rmdir "$work_dir"
if ! git clone --quiet --depth=1 --branch main "$feed_remote" "$work_dir"; then
  fail "SSH clone of ${feed_repository} failed"
fi
[[ ! -L "${work_dir}/manifest" ]] || fail 'existing pointer manifest is a symlink'
cp -- "$manifest_path" "${work_dir}/manifest.new"
mv -- "${work_dir}/manifest.new" "${work_dir}/manifest"
git -C "$work_dir" config user.name 'github-actions[bot]'
git -C "$work_dir" config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git -C "$work_dir" add -- manifest
if git -C "$work_dir" diff --cached --quiet -- manifest; then
  fail "${feed_repository} already contains this manifest; refusing to create a no-op pointer commit"
fi
git -C "$work_dir" commit --quiet -m "Publish ${channel} pointer for ${release_tag} (${source_sha:0:7})"
git -C "$work_dir" push origin HEAD:refs/heads/main
printf 'published %s channel pointer for %s to %s\n' "$channel" "$release_tag" "$feed_repository"
