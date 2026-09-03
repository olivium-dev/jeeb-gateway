#!/usr/bin/env bash
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
subject="$repo_root/scripts/verify-chat-b-activation-preflight.sh"
tmp_dir=$(mktemp -d)
trap 'rm -rf -- "$tmp_dir"' EXIT

expected_sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa

cat >"$tmp_dir/docker" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
command_line="$*"
case "$command_line" in
  "service inspect jeeb-staging-push-notification --format "*|"service inspect push-notification --format "*)
    if [[ "$command_line" == *ContainerSpec.Env* ]]; then
      printf 'PUSH_AUTH_MODE=%s\n' "${TEST_PUSH_MODE:-expand}"
    else
      printf 'ghcr.io/olivium-dev/push-notification@sha256:%064d\n' 1
    fi
    ;;
  "service inspect jeeb-staging-jeeb-gateway --format "*)
    if [[ "$command_line" == *ContainerSpec.Env* ]]; then
      printf 'FeatureFlags__UseUpstream__Chat=%s\n' "${TEST_CHAT_FLAG:-false}"
      printf 'PushNotificationServiceApi__GatewayApiKeyFile=/run/secrets/push_gateway_api_key\n'
      printf 'PushNotificationServiceApi__BaseUrl=http://jeeb-staging-push-notification:8080\n'
    elif [[ "$command_line" == *ContainerSpec.Secrets* ]]; then
      printf '%s|push_gateway_api_key|65532|65532|%s\n' \
        "${TEST_KEY_SOURCE:-jeeb_staging_gateway_push_token_12345_1}" \
        "${TEST_KEY_MODE:-256}"
    else
      printf 'ghcr.io/olivium-dev/jeeb-gateway@sha256:%064d\n' 2
    fi
    ;;
  "service inspect jeeb-production-jeeb-gateway --format "*)
    if [[ "$command_line" == *ContainerSpec.Env* ]]; then
      printf 'FeatureFlags__UseUpstream__Chat=%s\n' "${TEST_CHAT_FLAG:-false}"
      printf 'PushNotificationServiceApi__GatewayApiKeyFile=/run/secrets/push_gateway_api_key\n'
      printf 'PushNotificationServiceApi__BaseUrl=http://push-notification:8080\n'
    elif [[ "$command_line" == *ContainerSpec.Secrets* ]]; then
      printf '%s|push_gateway_api_key|65532|65532|%s\n' \
        "${TEST_KEY_SOURCE:-jeeb_gateway_push_token_12345_1}" \
        "${TEST_KEY_MODE:-256}"
    else
      printf 'ghcr.io/olivium-dev/jeeb-gateway@sha256:%064d\n' 2
    fi
    ;;
  "service ps jeeb-staging-jeeb-gateway --filter desired-state=running --format "*|"service ps jeeb-production-jeeb-gateway --filter desired-state=running --format "*)
    printf 'task123\n'
    ;;
  "inspect task123 --format "*)
    printf 'abcdef0123456789abcdef0123456789\n'
    ;;
  "inspect abcdef0123456789abcdef0123456789 --format "*)
    printf 'true\n'
    ;;
  "exec abcdef0123456789abcdef0123456789 sha256sum /run/secrets/push_gateway_api_key")
    printf '%s  /run/secrets/push_gateway_api_key\n' "${TEST_MOUNTED_SHA:-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa}"
    ;;
  "exec abcdef0123456789abcdef0123456789 sh -eu -c "*)
    printf '{"status":"%s","scope":"%s"}\n' \
      "${TEST_READY_STATUS:-ready}" "${TEST_READY_SCOPE:-gateway.registration}"
    ;;
  *)
    echo "unexpected fake docker call: $command_line" >&2
    exit 98
    ;;
esac
FAKE
chmod +x "$tmp_dir/docker"

run_case() {
  env PATH="$tmp_dir:$PATH" "$@" bash "$subject" \
    jeeb-staging-jeeb-gateway jeeb-staging-push-notification "$expected_sha"
}

run_case >/dev/null
env PATH="$tmp_dir:$PATH" bash "$subject" \
  jeeb-production-jeeb-gateway push-notification "$expected_sha" >/dev/null

for negative in \
  'TEST_PUSH_MODE=strict provider expand' \
  'TEST_CHAT_FLAG=true A1 Chat=false' \
  'TEST_KEY_MODE=292 ownership, or mode' \
  'TEST_MOUNTED_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb protected activation input' \
  'TEST_READY_SCOPE=notifications.send scope proof'; do
  assignment=${negative%% *}
  expected=${negative#* }
  if output=$(run_case "$assignment" 2>&1); then
    echo "FAIL: negative preflight case unexpectedly passed: $assignment" >&2
    exit 1
  fi
  grep -Fq "$expected" <<<"$output" || {
    echo "FAIL: negative preflight case was not discriminating: $assignment" >&2
    exit 1
  }
done

if output=$(env PATH="$tmp_dir:$PATH" bash "$subject" \
  unapproved-gateway unapproved-push "$expected_sha" 2>&1); then
  echo 'FAIL: unapproved service pair unexpectedly passed' >&2
  exit 1
fi
grep -Fq 'unapproved Jeeb Chat B service pair' <<<"$output"

echo 'Jeeb Chat B preflight executable positive and negative tests: PASS'
