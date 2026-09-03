#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT

pem_begin=$(printf '%s%s' '-----BEGIN PRIVATE ' 'KEY-----')
pem_end=$(printf '%s%s' '-----END PRIVATE ' 'KEY-----')
credential=$(printf \
  '{"type":"service_account","project_id":"jeeb-5a293","client_email":"firebase@example.invalid","private_key":"%s\\ntest\\n%s\\n"}' \
  "$pem_begin" "$pem_end")
expected_digest=$(printf '%s' "$credential" | sha256sum | awk '{print $1}')

raw_path="$test_root/raw.json"
raw_digest=$(printf '%s' "$credential" \
  | python3 "$repo_root/scripts/validate-firebase-service-account.py" \
      --materialize "$raw_path")
[ "$raw_digest" = "$expected_digest" ]
[ "$(stat -c '%a' "$raw_path" 2>/dev/null || stat -f '%Lp' "$raw_path")" = 600 ]
[ "$(cat "$raw_path")" = "$credential" ]

encoded_path="$test_root/base64.json"
encoded_digest=$(printf '%s' "$credential" | base64 | tr -d '\n' \
  | python3 "$repo_root/scripts/validate-firebase-service-account.py" \
      --materialize "$encoded_path")
[ "$encoded_digest" = "$expected_digest" ]
[ "$(cat "$encoded_path")" = "$credential" ]

reject() {
  local name=$1 document=$2
  if printf '%s' "$document" \
    | python3 "$repo_root/scripts/validate-firebase-service-account.py" \
        --materialize "$test_root/$name.json" >/dev/null 2>&1; then
    echo "validator accepted unsafe credential: $name" >&2
    exit 1
  fi
}

reject wrong-type \
  "$(printf '{\"type\":\"authorized_user\",\"project_id\":\"jeeb-5a293\",\"client_email\":\"x\",\"private_key\":\"%s\"}' "$pem_begin")"
reject wrong-project \
  "$(printf '{\"type\":\"service_account\",\"project_id\":\"other\",\"client_email\":\"x\",\"private_key\":\"%s\"}' "$pem_begin")"
reject missing-key \
  '{"type":"service_account","project_id":"jeeb-5a293","client_email":"x"}'
reject invalid-json 'not-json'

echo 'Firebase service-account validator tests: PASS'
