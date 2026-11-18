#!/usr/bin/env bash
# Poll one or more paths on a base URL until each returns HTTP 200.
#
# Usage: verify-endpoints.sh [--strict] [--timeout SECONDS] [--interval SECONDS] BASE_URL PATH...
#
# Every attempt is logged as "<path> attempt=<n> status=<code> elapsed=<s>s".
# Default (deploy) mode retries 000 (no response), 404 and 5xx until the timeout, because a
# restarting App Service can answer with any of them while the new container warms up.
# --strict (CI) retries only 000 and 5xx and fails immediately on anything else, e.g. a 404.
# On failure the failing path and last status are printed as a GitHub Actions error.
set -u

strict=0
timeout=600
interval=10
while [ $# -gt 0 ]; do
  case "$1" in
    --strict) strict=1; shift ;;
    --timeout) timeout="$2"; shift 2 ;;
    --interval) interval="$2"; shift 2 ;;
    --) shift; break ;;
    -*) echo "Unknown option: $1" >&2; exit 2 ;;
    *) break ;;
  esac
done

if [ $# -lt 2 ]; then
  echo "Usage: $0 [--strict] [--timeout S] [--interval S] BASE_URL PATH..." >&2
  exit 2
fi

base="${1%/}"
shift
errfile=$(mktemp)
trap 'rm -f "$errfile"' EXIT

for path in "$@"; do
  start=$SECONDS
  attempt=0
  status=000
  curl_error=""
  while :; do
    attempt=$((attempt + 1))
    status=$(curl --silent --show-error --output /dev/null --max-time 10 \
      --write-out '%{http_code}' "$base$path" 2>"$errfile") || true
    curl_error=$(tr '\n' ' ' <"$errfile")
    elapsed=$((SECONDS - start))
    echo "$path attempt=$attempt status=$status elapsed=${elapsed}s${curl_error:+ curl=\"$curl_error\"}"

    [ "$status" = "200" ] && break

    retryable=0
    case "$status" in
      000|5??) retryable=1 ;;
      404) [ "$strict" -eq 0 ] && retryable=1 ;;
    esac
    if [ "$retryable" -eq 0 ] || [ "$elapsed" -ge "$timeout" ]; then
      echo "::error::Endpoint check failed for $path: last HTTP status $status after ${elapsed}s (${attempt} attempts)${curl_error:+; curl: $curl_error}"
      exit 1
    fi
    sleep "$interval"
  done
  echo "OK $path -> 200"
done
