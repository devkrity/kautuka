#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/publish-release-feed.sh --feed-dir <dir>

Upload only the signed catalog tree to the existing S3-compatible object host.
Release installers, archives, and platform manifests are GitHub Release assets and are not uploaded here.

Required environment:
  RELEASE_FEED_S3_ENDPOINT
  RELEASE_FEED_S3_BUCKET
  RELEASE_FEED_S3_ACCESS_KEY_ID
  RELEASE_FEED_S3_SECRET_ACCESS_KEY

Optional:
  RELEASE_FEED_S3_REGION   Default: auto
  RELEASE_FEED_S3_PREFIX   Default: empty
EOF
}

feed_dir=''

while [[ $# -gt 0 ]]; do
  case "$1" in
    --feed-dir)
      feed_dir="$2"
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

if [[ -z "$feed_dir" ]]; then
  usage >&2
  exit 1
fi

if [[ ! -d "$feed_dir" ]]; then
  echo "error: feed directory not found: $feed_dir" >&2
  exit 1
fi

endpoint="${RELEASE_FEED_S3_ENDPOINT:-}"
bucket="${RELEASE_FEED_S3_BUCKET:-}"
region="${RELEASE_FEED_S3_REGION:-auto}"
prefix="${RELEASE_FEED_S3_PREFIX:-}"
access_key="${RELEASE_FEED_S3_ACCESS_KEY_ID:-}"
secret_key="${RELEASE_FEED_S3_SECRET_ACCESS_KEY:-}"

if [[ -z "$endpoint" || -z "$bucket" || -z "$access_key" || -z "$secret_key" ]]; then
  echo 'error: public feed publish requires RELEASE_FEED_S3_ENDPOINT, RELEASE_FEED_S3_BUCKET, RELEASE_FEED_S3_ACCESS_KEY_ID, and RELEASE_FEED_S3_SECRET_ACCESS_KEY.' >&2
  echo '       Configure catalog-host credentials in the selected GitHub Environment.' >&2
  exit 1
fi

if ! command -v aws >/dev/null 2>&1; then
  echo 'error: aws CLI is required to publish the public feed.' >&2
  exit 1
fi

prefix="${prefix#/}"
prefix="${prefix%/}"

content_type_for() {
  local relative_path="$1"
  local basename_value
  basename_value="$(basename "$relative_path")"

  case "$relative_path" in
    catalog/manifest)
      printf 'application/json; charset=utf-8\n'
      return
      ;;
  esac

  case "$basename_value" in
    *.json) printf 'application/json; charset=utf-8\n' ;;
    *.sig) printf 'text/plain; charset=utf-8\n' ;;
    *.sh) printf 'text/x-shellscript; charset=utf-8\n' ;;
    *.gz) printf 'application/gzip\n' ;;
    *.exe) printf 'application/vnd.microsoft.portable-executable\n' ;;
    *.txt) printf 'text/plain; charset=utf-8\n' ;;
    *.md) printf 'text/markdown; charset=utf-8\n' ;;
    *) printf 'application/octet-stream\n' ;;
  esac
}

cache_control_for() {
  local relative_path="$1"
  case "$relative_path" in
    catalog/manifest|catalog/manifest.sig)
      printf 'public, max-age=60\n'
      ;;
    *)
      printf 'public, max-age=31536000, immutable\n'
      ;;
  esac
}

export AWS_ACCESS_KEY_ID="$access_key"
export AWS_SECRET_ACCESS_KEY="$secret_key"
export AWS_DEFAULT_REGION="$region"
export AWS_REQUEST_CHECKSUM_CALCULATION=WHEN_REQUIRED
export AWS_RESPONSE_CHECKSUM_VALIDATION=WHEN_REQUIRED

feed_dir="$(cd "$feed_dir" && pwd)"
uploaded=0

while IFS= read -r -d '' file_path; do
  relative_path="${file_path#"$feed_dir"/}"
  if [[ "$relative_path" != catalog/* ]]; then
    echo "error: S3 publisher accepts catalog assets only, got: $relative_path" >&2
    exit 1
  fi
  key="$relative_path"
  if [[ -n "$prefix" ]]; then
    key="${prefix}/${relative_path}"
  fi

  content_type="$(content_type_for "$relative_path")"
  cache_control="$(cache_control_for "$relative_path")"

  aws s3 cp "$file_path" "s3://${bucket}/${key}" \
    --endpoint-url "$endpoint" \
    --content-type "$content_type" \
    --cache-control "$cache_control" \
    --only-show-errors

  uploaded=$((uploaded + 1))
  printf 'uploaded %s -> s3://%s/%s (%s)\n' "$relative_path" "$bucket" "$key" "$content_type"
done < <(find "$feed_dir" -type f -print0 | sort -z)

if [[ "$uploaded" -eq 0 ]]; then
  echo "error: feed directory contained no files: $feed_dir" >&2
  exit 1
fi

printf 'published %s objects to s3://%s/%s\n' "$uploaded" "$bucket" "${prefix:-<bucket-root>}"
