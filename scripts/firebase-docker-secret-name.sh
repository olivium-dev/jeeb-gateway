#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 2 ] || {
  echo 'usage: firebase-docker-secret-name.sh <prefix> <sha256>' >&2
  exit 64
}

prefix=$1
digest=$2
[[ "$prefix" =~ ^[a-zA-Z0-9_.-]+$ ]] || {
  echo 'Firebase Docker secret prefix contains unsupported characters' >&2
  exit 64
}
[[ "$digest" =~ ^[0-9a-f]{64}$ ]] || {
  echo 'Firebase credential digest must be a lowercase SHA-256 value' >&2
  exit 64
}

digest_suffix=$(python3 -c \
  'import base64, sys; print(base64.urlsafe_b64encode(bytes.fromhex(sys.argv[1])).decode("ascii").rstrip("="))' \
  "$digest")
[[ "$digest_suffix" =~ ^[a-zA-Z0-9_-]{43}$ ]]

secret_name="${prefix}${digest_suffix}"
if [ "${#secret_name}" -gt 64 ]; then
  echo 'Firebase Docker secret name exceeds the 64-character limit' >&2
  exit 64
fi
[[ "$secret_name" =~ ^[a-zA-Z0-9_.-]+$ ]]
printf '%s\n' "$secret_name"
