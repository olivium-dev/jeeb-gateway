#!/usr/bin/env bash

# Source this file from a strict Bash shell. The remote flock is held by a
# dedicated SSH process, so runner cancellation or connection loss releases it
# without a stale-lock takeover path.

STAGING_GATEWAY_LOCK_HELD=false
STAGING_GATEWAY_LOCK_SSH_ALIAS=''
STAGING_GATEWAY_LOCK_PRIVATE_ROOT=''
STAGING_GATEWAY_LOCK_OWNER_FILE=''
STAGING_GATEWAY_LOCK_ERROR_FILE=''
STAGING_GATEWAY_LOCK_INPUT_FIFO=''
STAGING_GATEWAY_LOCK_OUTPUT_FIFO=''
STAGING_GATEWAY_LOCK_PID=''
STAGING_GATEWAY_LOCK_WRITE_FD=8
STAGING_GATEWAY_LOCK_READ_FD=9
STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS=${STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS:-90}

staging_gateway_lock_init() {
  local ssh_alias=$1 private_root=$2
  [[ "$ssh_alias" =~ ^[a-zA-Z0-9_.-]+$ ]]
  [ -d "$private_root" ]
  [[ "$STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ ]]
  [ "$STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS" -le 300 ]

  STAGING_GATEWAY_LOCK_SSH_ALIAS=$ssh_alias
  STAGING_GATEWAY_LOCK_PRIVATE_ROOT=$private_root
  STAGING_GATEWAY_LOCK_OWNER_FILE="$private_root/staging-gateway-lock.owner"
  STAGING_GATEWAY_LOCK_ERROR_FILE="$private_root/staging-gateway-lock.stderr"
  STAGING_GATEWAY_LOCK_INPUT_FIFO="$private_root/staging-gateway-lock.input"
  STAGING_GATEWAY_LOCK_OUTPUT_FIFO="$private_root/staging-gateway-lock.output"
  (umask 077; : > "$STAGING_GATEWAY_LOCK_ERROR_FILE")
}

staging_gateway_lock_assert() {
  local owner
  [ "$STAGING_GATEWAY_LOCK_HELD" = true ]
  [[ "$STAGING_GATEWAY_LOCK_PID" =~ ^[1-9][0-9]*$ ]]
  kill -0 "$STAGING_GATEWAY_LOCK_PID" 2>/dev/null
  owner=$(<"$STAGING_GATEWAY_LOCK_OWNER_FILE")
  [[ "$owner" =~ ^[0-9a-f]{64}$ ]]
  printf '%s\n' "$owner" | ssh "$STAGING_GATEWAY_LOCK_SSH_ALIAS" 'set -euo pipefail
    lock_owner_file="$HOME/.jeeb-deploy/locks/jeeb-staging-gateway.owner"
    IFS= read -r expected_owner
    [[ "$expected_owner" =~ ^[0-9a-f]{64}$ ]]
    [ "$(cat "$lock_owner_file" 2>/dev/null)" = "$expected_owner" ]'
}

staging_gateway_lock_acquire() {
  local acquired owner read_timeout remote_command
  local remote_script
  [ "$STAGING_GATEWAY_LOCK_HELD" = false ]
  : "${STAGING_GATEWAY_LOCK_SSH_ALIAS:?staging_gateway_lock_init must run first}"
  : "${STAGING_GATEWAY_LOCK_PRIVATE_ROOT:?staging_gateway_lock_init must run first}"

  (umask 077; openssl rand -hex 32 > "$STAGING_GATEWAY_LOCK_OWNER_FILE")
  owner=$(<"$STAGING_GATEWAY_LOCK_OWNER_FILE")
  [[ "$owner" =~ ^[0-9a-f]{64}$ ]]
  read -r -d '' remote_script <<'REMOTE' || true
set -euo pipefail
: "${LOCK_WAIT_SECONDS:?LOCK_WAIT_SECONDS is required}"
[[ "$LOCK_WAIT_SECONDS" =~ ^[1-9][0-9]*$ ]]
lock_dir="$HOME/.jeeb-deploy/locks"
lock_file="$lock_dir/jeeb-staging-gateway.lock"
lock_owner_file="$lock_dir/jeeb-staging-gateway.owner"
install -d -m 700 "$HOME/.jeeb-deploy" "$lock_dir"
IFS= read -r expected_owner
[[ "$expected_owner" =~ ^[0-9a-f]{64}$ ]]
exec 9>"$lock_file"
chmod 600 "$lock_file"
flock -w "$LOCK_WAIT_SECONDS" 9 || exit 75
cleanup_lock_owner() {
  status=$?
  trap - EXIT HUP INT TERM
  current_owner=$(cat "$lock_owner_file" 2>/dev/null || true)
  if [ "$current_owner" = "$expected_owner" ]; then
    rm -f -- "$lock_owner_file" || status=76
  else
    status=76
  fi
  exit "$status"
}
trap cleanup_lock_owner EXIT HUP INT TERM
umask 077
printf "%s\n" "$expected_owner" > "$lock_owner_file"
printf "LOCKED\n"
IFS= read -r release_owner
[ "$release_owner" = "$expected_owner" ]
REMOTE
  remote_command="LOCK_WAIT_SECONDS=$STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS bash -c '$remote_script'"

  mkfifo "$STAGING_GATEWAY_LOCK_INPUT_FIFO" "$STAGING_GATEWAY_LOCK_OUTPUT_FIFO"
  chmod 600 "$STAGING_GATEWAY_LOCK_INPUT_FIFO" "$STAGING_GATEWAY_LOCK_OUTPUT_FIFO"
  exec 8<>"$STAGING_GATEWAY_LOCK_INPUT_FIFO"
  exec 9<>"$STAGING_GATEWAY_LOCK_OUTPUT_FIFO"
  # shellcheck disable=SC2029  # The quoted script is intentionally expanded by the remote shell.
  ssh "$STAGING_GATEWAY_LOCK_SSH_ALIAS" "$remote_command" \
    < "$STAGING_GATEWAY_LOCK_INPUT_FIFO" \
    > "$STAGING_GATEWAY_LOCK_OUTPUT_FIFO" \
    2>"$STAGING_GATEWAY_LOCK_ERROR_FILE" &
  STAGING_GATEWAY_LOCK_PID=$!

  if ! printf '%s\n' "$owner" >&"$STAGING_GATEWAY_LOCK_WRITE_FD"; then
    acquired=''
  else
    read_timeout=$((STAGING_GATEWAY_LOCK_TIMEOUT_SECONDS + 10))
    IFS= read -r -t "$read_timeout" acquired <&"$STAGING_GATEWAY_LOCK_READ_FD" \
      || acquired=''
  fi
  if [ "$acquired" != LOCKED ]; then
    exec 8>&- || true
    kill "$STAGING_GATEWAY_LOCK_PID" 2>/dev/null || true
    wait "$STAGING_GATEWAY_LOCK_PID" 2>/dev/null || true
    exec 9<&- || true
    STAGING_GATEWAY_LOCK_PID=''
    echo 'RED: timed out or failed to acquire the staging gateway mutation lock' >&2
    return 1
  fi

  STAGING_GATEWAY_LOCK_HELD=true
  if ! staging_gateway_lock_assert; then
    echo 'RED: staging gateway mutation lock ownership could not be proven' >&2
    staging_gateway_lock_release >/dev/null 2>&1 || true
    return 1
  fi
}

staging_gateway_lock_release() {
  local owner release_ok=true
  [ "$STAGING_GATEWAY_LOCK_HELD" = true ] || {
    echo 'RED: staging gateway mutation lock is not held' >&2
    return 1
  }
  owner=$(<"$STAGING_GATEWAY_LOCK_OWNER_FILE")
  staging_gateway_lock_assert || release_ok=false
  if ! printf '%s\n' "$owner" >&"$STAGING_GATEWAY_LOCK_WRITE_FD"; then
    release_ok=false
  fi
  exec 8>&- || release_ok=false
  wait "$STAGING_GATEWAY_LOCK_PID" || release_ok=false
  exec 9<&- || release_ok=false
  STAGING_GATEWAY_LOCK_HELD=false
  STAGING_GATEWAY_LOCK_PID=''
  [ "$release_ok" = true ] || {
    echo 'RED: staging gateway mutation lock ownership-safe cleanup failed' >&2
    return 1
  }
}
