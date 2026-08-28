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
url=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --output) response_file=$2; shift 2 ;;
    https://*) url=$1; shift ;;
    *) shift ;;
  esac
done
[ -n "$response_file" ]
if [ "$PUBLIC_PROBE_FAILURE" = transport ]; then
  exit 7
fi
if [ "$PUBLIC_PROBE_FAILURE" = devtool ]; then
  case "$url" in
    */admin/v1/auth/refresh|*/gateway/admin/v1/auth/refresh)
      : > "$PUBLIC_PROBE_FORBIDDEN"
      exit 70
      ;;
    */api/User/super-login/users)
      printf '%s\n' '{"users":[]}' > "$response_file"; status=200 ;;
    */api/User/demo-users|*/dev/data/users)
      printf '%s\n' '{}' > "$response_file"; status=200 ;;
    */v1/auth/otp/request|*/auth/tokens)
      printf '%s\n' '{}' > "$response_file"; status=400 ;;
    */swagger/index.html|*/swagger/v1/swagger.json)
      printf '%s\n' '{}' > "$response_file"; status=404 ;;
    *) exit 71 ;;
  esac
  printf '%s' "$status"
  exit 0
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

devtool_log="$test_root/devtool.log"
PUBLIC_PROBE_FAILURE=devtool PUBLIC_PROBE_FORBIDDEN="$test_root/forbidden" \
  PATH="$fake_bin:$PATH" bash "$subject" devtool >"$devtool_log" 2>&1
[ ! -e "$test_root/forbidden" ]
grep -Fq 'Staging public OTP and devtool contracts are exact.' "$devtool_log"

devtool_posture_log="$test_root/devtool-posture.log"
PUBLIC_PROBE_FAILURE=devtool PUBLIC_PROBE_FORBIDDEN="$test_root/forbidden" \
  PATH="$fake_bin:$PATH" bash "$subject" devtool-posture true true true true true \
  >"$devtool_posture_log" 2>&1
[ ! -e "$test_root/forbidden" ]
grep -Fq 'Staging public OTP and devtool contracts are exact.' "$devtool_posture_log"

echo 'staging public probe diagnostics: PASS (curl/type visible, bodies redacted, Dev Tool candidate/recovery security scope excluded)'
