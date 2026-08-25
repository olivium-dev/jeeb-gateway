#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
test_root=$(mktemp -d)
fake_ssh="$test_root/ssh"

cleanup() {
  rm -f -- "$fake_ssh"
  rmdir -- "$test_root"
}
trap cleanup EXIT

cat > "$fake_ssh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
case "${FAKE_SSH_RESULT:?}" in
  failure) exit 255 ;;
  empty) exit 0 ;;
  multiline) printf '%s\n%s\n' production-gateway extra ;;
  unsafe) printf '%s\n' 'production gateway' ;;
  staging) printf '%s\n' jeeb-staging-jeeb-gateway ;;
  production) printf '%s\n' jeeb-production-jeeb-gateway ;;
  *) exit 70 ;;
esac
FAKE
chmod 700 "$fake_ssh"

guard="$repository_root/scripts/reject-staging-gateway-alias.sh"
for rejected in failure empty multiline unsafe staging; do
  if FAKE_SSH_RESULT="$rejected" PATH="$test_root:$PATH" \
    bash "$guard" service-or-id >/dev/null 2>&1; then
    echo "alias guard accepted rejected authority: $rejected" >&2
    exit 1
  fi
done

FAKE_SSH_RESULT=production PATH="$test_root:$PATH" \
  bash "$guard" service-or-id >/dev/null

if FAKE_SSH_RESULT=production PATH="$test_root:$PATH" \
  bash "$guard" 'bad service' >/dev/null 2>&1; then
  echo 'alias guard accepted an unsafe requested service' >&2
  exit 1
fi

echo 'Generic staging alias guard adversarial tests PASSED (7 cases)'
