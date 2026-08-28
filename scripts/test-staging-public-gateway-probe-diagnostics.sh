#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
subject="$repository_root/scripts/probe-staging-public-gateway-contract.sh"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
fake_bin="$test_root/bin"
mkdir "$fake_bin"

cat > "$fake_bin/curl" <<'CURL'
#!/usr/bin/env bash
set -euo pipefail
response_file=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --output) response_file=$2; shift 2 ;;
    *) shift ;;
  esac
done
[ -n "$response_file" ]
if [ "$PUBLIC_PROBE_FAILURE" = transport ]; then
  exit 7
fi
printf '%s\n' '{"type":"RESPONSE_BODY_CANARY_MUST_NOT_BE_LOGGED"}' > "$response_file"
printf '%s' 403
CURL
chmod +x "$fake_bin/curl"

for failure in transport type; do
  log="$test_root/${failure}.log"
  if PUBLIC_PROBE_FAILURE=$failure PATH="$fake_bin:$PATH" \
      bash "$subject" invariant >"$log" 2>&1; then
    echo "public gateway probe accepted ${failure} failure" >&2
    exit 1
  fi
  if grep -Fq 'RESPONSE_BODY_CANARY_MUST_NOT_BE_LOGGED' "$log"; then exit 1; fi
done
grep -Fq 'curl transport failed during origin contract probe' "$test_root/transport.log"
grep -Fq 'HTTP 403 problem type did not match the expected origin contract' "$test_root/type.log"

echo 'staging public probe diagnostics: PASS (curl/type failures visible, response bodies redacted)'
