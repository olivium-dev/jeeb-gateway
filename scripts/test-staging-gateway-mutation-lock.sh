#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
harness_root=$(mktemp -d)
fake_remote_root="$harness_root/remote"
mkdir -m 700 "$fake_remote_root"
cleanup() {
  rm -rf -- "$harness_root"
}
trap cleanup EXIT

# Replace only the SSH boundary. This fake uses the host flock implementation,
# so concurrent helpers exercise the checked-in holder protocol rather than a
# lexical lock model.
ssh() {
  [ "$1" = jeeb-staging ]
  shift
  remote_command=$*
  if [[ "$remote_command" == *"LOCK_WAIT_SECONDS="* ]]; then
    lock_wait=$(printf '%s\n' "$remote_command" \
      | sed -n 's/^LOCK_WAIT_SECONDS=\([0-9][0-9]*\) .*$/\1/p')
    [[ "$lock_wait" =~ ^[1-9][0-9]*$ ]]
    IFS= read -r expected_owner
    [[ "$expected_owner" =~ ^[0-9a-f]{64}$ ]]
    exec 9>"$fake_remote_root/jeeb-staging-gateway.lock"
    flock -w "$lock_wait" 9 || return 75
    # shellcheck disable=SC2329  # Invoked indirectly by the EXIT/signal trap.
    cleanup_owner() {
      status=$?
      trap - EXIT HUP INT TERM
      current_owner=$(cat "$fake_remote_root/jeeb-staging-gateway.owner" 2>/dev/null || true)
      if [ "$current_owner" = "$expected_owner" ]; then
        rm -f -- "$fake_remote_root/jeeb-staging-gateway.owner" || status=76
      else
        status=76
      fi
      exit "$status"
    }
    trap cleanup_owner EXIT HUP INT TERM
    (umask 077; printf '%s\n' "$expected_owner" \
      > "$fake_remote_root/jeeb-staging-gateway.owner")
    printf 'LOCKED\n'
    IFS= read -r release_owner
    [ "$release_owner" = "$expected_owner" ]
    current_owner=$(cat "$fake_remote_root/jeeb-staging-gateway.owner" 2>/dev/null || true)
    [ "$current_owner" = "$expected_owner" ] || return 76
    rm -f -- "$fake_remote_root/jeeb-staging-gateway.owner"
    trap - EXIT HUP INT TERM
    return
  fi

  IFS= read -r expected_owner
  [[ "$expected_owner" =~ ^[0-9a-f]{64}$ ]]
  [ "$(cat "$fake_remote_root/jeeb-staging-gateway.owner" 2>/dev/null)" = "$expected_owner" ]
}

new_private_root() {
  mktemp -d "$harness_root/private.XXXXXX"
}

STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS=2
# shellcheck disable=SC1091  # The repository root is computed at runtime.
source "$repository_root/scripts/staging-gateway-mutation-lock.sh"
private_root=$(new_private_root)
staging_gateway_lock_init jeeb-staging "$private_root"
staging_gateway_lock_acquire
staging_gateway_lock_assert
staging_gateway_lock_release
[ ! -e "$fake_remote_root/jeeb-staging-gateway.owner" ]
echo 'lock case acquire/release: PASS'

# Loss of the persistent SSH holder must make assertion and cleanup RED.
private_root=$(new_private_root)
staging_gateway_lock_init jeeb-staging "$private_root"
staging_gateway_lock_acquire
kill "$STAGING_GATEWAY_LOCK_PID"
wait "$STAGING_GATEWAY_LOCK_PID" 2>/dev/null || true
set +e
staging_gateway_lock_assert >/dev/null 2>&1
loss_status=$?
staging_gateway_lock_release >/dev/null 2>&1
loss_cleanup_status=$?
set -e
[ "$loss_status" -ne 0 ]
[ "$loss_cleanup_status" -ne 0 ]
echo 'lock case holder loss: PASS'

# Owner-file replacement must prevent ownership-unsafe cleanup.
private_root=$(new_private_root)
staging_gateway_lock_init jeeb-staging "$private_root"
staging_gateway_lock_acquire
printf '%064d\n' 0 > "$fake_remote_root/jeeb-staging-gateway.owner"
set +e
staging_gateway_lock_release >/dev/null 2>&1
owner_cleanup_status=$?
set -e
[ "$owner_cleanup_status" -ne 0 ]
echo 'lock case ownership-safe cleanup: PASS'

# A second workflow using the same remote flock must time out without mutation.
private_root=$(new_private_root)
staging_gateway_lock_init jeeb-staging "$private_root"
staging_gateway_lock_acquire
set +e
(
  # Consumed while sourcing the lock helper.
  # shellcheck disable=SC2034
  STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS=1
  # shellcheck disable=SC1091  # The repository root is computed at runtime.
  source "$repository_root/scripts/staging-gateway-mutation-lock.sh"
  contender_root=$(new_private_root)
  staging_gateway_lock_init jeeb-staging "$contender_root"
  staging_gateway_lock_acquire
) >/dev/null 2>&1
contender_status=$?
set -e
[ "$contender_status" -ne 0 ]
staging_gateway_lock_assert
staging_gateway_lock_release
[ ! -e "$fake_remote_root/jeeb-staging-gateway.owner" ]
echo 'lock case contention timeout: PASS'

echo 'staging gateway mutation lock tests: PASS (acquire/release, contention timeout, loss, owner-safe cleanup)'
