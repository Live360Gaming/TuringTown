#!/usr/bin/env bash
# Resolve a tag in GHCR to the digest of the manifest it points at, using the registry API.
#
# Usage: ghcr-resolve.sh [--require-amd64] REPOSITORY TAG     (e.g. live360gaming/turingtown v0.0.1)
# Prints the sha256 digest on stdout.
# Auth (optional, for private packages): GHCR_USER and GHCR_TOKEN.
# Exit codes: 0 resolved, 3 tag not found, 4 index has no linux/amd64 manifest, 1 other error.
set -eu

require_amd64=0
if [ "${1:-}" = "--require-amd64" ]; then require_amd64=1; shift; fi
[ $# -eq 2 ] || { echo "Usage: $0 [--require-amd64] REPOSITORY TAG" >&2; exit 2; }
repo="$1"
tag="$2"

auth=()
[ -n "${GHCR_TOKEN:-}" ] && auth=(--user "${GHCR_USER:-token}:$GHCR_TOKEN")

token=$(curl --silent --show-error --fail "${auth[@]}" \
  "https://ghcr.io/token?scope=repository:$repo:pull" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[ -n "$token" ] || { echo "Could not obtain a GHCR pull token for $repo" >&2; exit 1; }

headers=$(mktemp)
body=$(mktemp)
trap 'rm -f "$headers" "$body"' EXIT

code=$(curl --silent --show-error --location --output "$body" --dump-header "$headers" \
  --write-out '%{http_code}' \
  -H "Authorization: Bearer $token" \
  -H "Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json" \
  "https://ghcr.io/v2/$repo/manifests/$tag")

case "$code" in
  200) ;;
  404) echo "Tag $repo:$tag not found in GHCR" >&2; exit 3 ;;
  *) echo "Unexpected HTTP $code resolving $repo:$tag" >&2; exit 1 ;;
esac

digest=$(tr -d '\r' <"$headers" | awk 'tolower($1)=="docker-content-digest:" {print $2}' | tail -n1)
case "$digest" in sha256:*) ;; *) echo "Registry returned no digest for $repo:$tag" >&2; exit 1 ;; esac

if [ "$require_amd64" -eq 1 ]; then
  # Whitespace-insensitive match of an index entry with platform linux/amd64.
  tr -d ' \n\r\t' <"$body" | grep -q '"architecture":"amd64","os":"linux"\|"os":"linux","architecture":"amd64"' \
    || { echo "Index $digest has no linux/amd64 manifest" >&2; exit 4; }
fi

echo "$digest"
