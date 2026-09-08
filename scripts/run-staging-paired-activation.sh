#!/usr/bin/env bash
# Runner-side adapter. Called only by the separately reviewed workflow candidate.
# Remote source and runtime.json must already be staged under exclusive custody.
set -euo pipefail

script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$script_root/staging-delivery-paired-activation-draft.sh"
: "${GITHUB_RUN_ID:?}" "${GITHUB_RUN_ATTEMPT:?}" "${GITHUB_SHA:?}" "${GH_TOKEN:?}"
[[ "$GITHUB_RUN_ID" =~ ^[1-9][0-9]*$ && "$GITHUB_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ ]]
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]]
remote_root=".jeeb-deploy/paired-runtime-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
local_root=$(mktemp -d)
chmod 700 "$local_root"
lock_owner=$(openssl rand -hex 32)
lock_pid=''
lock_held=false

cleanup() {
  status=$?
  trap - EXIT HUP INT TERM
  if [ "$lock_held" = true ]; then
    printf '%s\n' "$lock_owner" >&8 || status=1
    exec 8>&-
    wait "$lock_pid" || status=1
    exec 9<&-
  fi
  # Full specs are private ephemeral evidence; permanent journal/claims are
  # remote, outside this directory, and never deleted by this trap.
  find "$local_root" -maxdepth 1 -type f -delete
  rmdir "$local_root" || status=1
  exit "$status"
}
trap cleanup EXIT HUP INT TERM

paired_source_guard() {
  bash "$script_root/staging-paired-source-guard.sh"
}

remote() {
  # Arguments are solely fixed command names, roles, versions and validated
  # basenames generated here; no credentials, DSNs, or arbitrary shell input.
  ssh jeeb-staging python3 -I "$remote_root/staging-paired-engine.py" "$remote_root" "$@"
}
staging_gateway_lock_assert() {
  [ "$lock_held" = true ] && kill -0 "$lock_pid" 2>/dev/null
  remote lock
}
paired_require_current_protected_builds() { paired_source_guard && remote authority; }
paired_require_exact_daemon() { remote daemon; }
paired_require_existing_credential() { remote secret; }
paired_validate_narrow_candidates() { remote candidates; }
paired_probe_delivery_schema() { remote schema; }
paired_journal_begin() { remote begin; }
paired_journal_advance() { remote advance "$1"; }
paired_verify_gateway() { remote gateway; }
paired_verify_delivery() { remote delivery; }
paired_verify_authenticated_wire() { remote wire; }
paired_verify_both_final() { remote final; }
paired_capture_role() {
  local role=$1 path name
  shift
  local -a remote_paths=()
  for path in "$@"; do
    [ "$(dirname "$path")" = "$local_root" ]
    name=$(basename "$path")
    [[ "$name" =~ ^[a-z-]+(\.json)?$ ]]
    remote_paths+=("$remote_root/$name")
  done
  remote capture "$role" "${remote_paths[@]}" || return 1
  for path in "$@"; do
    scp -q "jeeb-staging:$remote_root/$(basename "$path")" "$path" || return 1
    chmod 600 "$path"
  done
}
paired_submit_role_cas() {
  local role=$1 sid=$2 version=$3 candidate_file=$4
  [[ "$role" = gateway || "$role" = delivery ]]
  [[ "$sid" =~ ^[a-z0-9]{25}$ && "$version" =~ ^[1-9][0-9]*$ ]]
  [ "$candidate_file" = "$local_root/$role-candidate.json" ]
  paired_source_guard || return 1
  remote submit "$role" "$sid" "$version" "$remote_root/$role-candidate.json"
}

paired_source_guard
# This holder uses strict nontruncating openat/O_EXCL custody. A stale owner is
# rejected rather than overwritten, and disconnect releases only the flock.
coproc PAIR_LOCK { ssh jeeb-staging python3 -I "$remote_root/staging-paired-custody.py" hold-lock; }
lock_pid=$PAIR_LOCK_PID
exec 8>&"${PAIR_LOCK[1]}" 9<&"${PAIR_LOCK[0]}"
printf '%s\n' "$lock_owner" >&8
IFS= read -r -t 15 acknowledgement <&9
[ "$acknowledgement" = LOCKED ]
lock_held=true
paired_source_guard
# The remote preparer binds the same owner into its private runtime state. The
# JSON payload is runner-private; only the noncredential lock nonce is inserted.
: "${PAIRED_PAYLOAD_FILE:?}"
[ -f "$PAIRED_PAYLOAD_FILE" ] && [ ! -L "$PAIRED_PAYLOAD_FILE" ]
jq --arg owner "$lock_owner" '. + {lockOwner:$owner}' "$PAIRED_PAYLOAD_FILE" \
  | ssh jeeb-staging python3 -I "$remote_root/staging-paired-engine.py" "$remote_root" prepare
for role in gateway delivery; do
  for suffix in incumbent.json incumbent-id incumbent-version candidate.json; do
    scp -q "jeeb-staging:$remote_root/$role-$suffix" "$local_root/$role-$suffix"
    chmod 600 "$local_root/$role-$suffix"
  done
done
staging_delivery_paired_activation_draft "$local_root"
