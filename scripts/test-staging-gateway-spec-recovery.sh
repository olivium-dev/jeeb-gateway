#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
harness_root=$(mktemp -d)
cleanup() {
  rm -rf -- "$harness_root"
}
trap cleanup EXIT

incumbent_spec="$harness_root/incumbent.json"
candidate_spec="$harness_root/candidate.json"
third_spec="$harness_root/third.json"
incumbent_version="$harness_root/incumbent.version"
candidate_version="$harness_root/candidate.version"
incumbent_id="$harness_root/incumbent.id"
candidate_id="$harness_root/candidate.id"
state_spec="$harness_root/state.json"
state_version="$harness_root/state.version"
state_id="$harness_root/state.id"

printf '%s\n' '{"Name":"gateway","TaskTemplate":{"ContainerSpec":{"Image":"repo@good"}},"Labels":{"state":"incumbent"}}' > "$incumbent_spec"
printf '%s\n' '{"Name":"gateway","TaskTemplate":{"ContainerSpec":{"Image":"repo@candidate"}},"Labels":{"state":"candidate"}}' > "$candidate_spec"
printf '%s\n' '{"Name":"gateway","TaskTemplate":{"ContainerSpec":{"Image":"repo@third"}},"Labels":{"state":"third"}}' > "$third_spec"
printf '%s\n' 40 > "$incumbent_version"
printf '%s\n' 41 > "$candidate_version"
printf '%s\n' serviceabc > "$incumbent_id"
printf '%s\n' serviceabc > "$candidate_id"

capture_count=0
cas_count=0
cas_count_file="$harness_root/cas.count"
apply_count_file="$harness_root/apply.count"
pending_seen_file="$harness_root/pending.seen"
last_forward_result="$harness_root/last-forward-result"
transition_on_capture=0
capture_fail_on=0
cas_mode=apply_200
verify_mode=pass

reset_candidate() {
  cp "$candidate_spec" "$state_spec"
  cp "$candidate_version" "$state_version"
  cp "$candidate_id" "$state_id"
  capture_count=0
  cas_count=0
  printf '%s\n' 0 > "$cas_count_file"
  printf '%s\n' 0 > "$apply_count_file"
  printf '%s\n' 0 > "$pending_seen_file"
  rm -f -- "$last_forward_result"
  transition_on_capture=0
  capture_fail_on=0
  cas_mode=apply_200
  verify_mode=pass
}

reset_incumbent() {
  cp "$incumbent_spec" "$state_spec"
  cp "$incumbent_version" "$state_version"
  cp "$incumbent_id" "$state_id"
  capture_count=0
  cas_count=0
  printf '%s\n' 0 > "$cas_count_file"
  printf '%s\n' 0 > "$apply_count_file"
  printf '%s\n' 0 > "$pending_seen_file"
  rm -f -- "$last_forward_result"
  printf '%s\n' 41 > "$candidate_version"
  transition_on_capture=0
  capture_fail_on=0
  cas_mode=apply_200
  verify_mode=pass
}

staging_gateway_lock_assert() {
  return 0
}

capture_remote_spec() {
  local destination=$1 version_destination=$2 id_destination=$3
  capture_count=$((capture_count + 1))
  if [ "$transition_on_capture" -eq "$capture_count" ]; then
    cp "$third_spec" "$state_spec"
    printf '%s\n' 42 > "$state_version"
  fi
  [ "$capture_fail_on" -ne "$capture_count" ] || return 1
  cp "$state_spec" "$destination"
  cp "$state_version" "$version_destination"
  cp "$state_id" "$id_destination"
}

apply_replacement() {
  local replacement=$1 next_version
  next_version=$(( $(<"$state_version") + 1 ))
  cp "$replacement" "$state_spec"
  printf '%s\n' "$next_version" > "$state_version"
  printf '%s\n' "$(( $(<"$apply_count_file") + 1 ))" > "$apply_count_file"
}

staging_gateway_submit_spec_cas() {
  local service_id=$1 expected_version=$2 replacement_spec=$3
  cas_count=$(( $(<"$cas_count_file") + 1 ))
  printf '%s\n' "$cas_count" > "$cas_count_file"
  [ "$service_id" = "$(<"$state_id")" ]
  if cmp -s "$replacement_spec" "$candidate_spec"; then
    [ -n "${expected_forward_result:-}" ]
    [ "$(<"$expected_forward_result")" = submitted-pending-reconciliation ]
    printf '%s\n' "$(( $(<"$pending_seen_file") + 1 ))" > "$pending_seen_file"
  else
    cmp -s "$replacement_spec" "$incumbent_spec"
  fi
  case "$cas_mode" in
    apply_200)
      [ "$(<"$state_version")" = "$expected_version" ] || {
        printf '%s\n' 409
        return
      }
      apply_replacement "$replacement_spec"
      printf '%s\n' 200
      ;;
    apply_lost)
      [ "$(<"$state_version")" = "$expected_version" ] || return 1
      apply_replacement "$replacement_spec"
      return 1
      ;;
    lost_without_apply_then_200)
      if [ "$cas_count" -eq 1 ]; then
        return 1
      fi
      [ "$(<"$state_version")" = "$expected_version" ] || {
        printf '%s\n' 409
        return
      }
      apply_replacement "$replacement_spec"
      printf '%s\n' 200
      ;;
    conflict_exact_candidate)
      cp "$candidate_spec" "$state_spec"
      printf '%s\n' 41 > "$state_version"
      printf '%s\n' 409
      ;;
    conflict)
      cp "$third_spec" "$state_spec"
      printf '%s\n' 42 > "$state_version"
      printf '%s\n' 409
      ;;
    *) return 1 ;;
  esac
}

staging_gateway_verify_incumbent() {
  local observed_spec=$1 observed_version=$2 observed_id=$3
  [ "$verify_mode" = pass ] || return 1
  cmp -s "$observed_spec" "$incumbent_spec"
  [ "$(<"$observed_version")" -ge 40 ]
  cmp -s "$observed_id" "$incumbent_id"
  cmp -s "$state_spec" "$incumbent_spec"
}

# shellcheck disable=SC1091
source "$repository_root/scripts/staging-gateway-spec-recovery.sh"

recover() {
  local work_root
  work_root=$(mktemp -d "$harness_root/recovery.XXXXXX")
  (
    set -euo pipefail
    staging_gateway_external_gate_recover \
      "$incumbent_spec" "$incumbent_version" "$incumbent_id" \
      "$candidate_spec" "$candidate_version" "$candidate_id" "$work_root"
  )
}

forward() {
  local work_root result_file status
  work_root=$(mktemp -d "$harness_root/forward.XXXXXX")
  result_file="$work_root/result"
  expected_forward_result=$result_file
  if (
    set -euo pipefail
    staging_gateway_forward_apply \
      "$incumbent_spec" "$incumbent_version" "$incumbent_id" \
      "$candidate_spec" "$candidate_version" "$candidate_id" \
      "$work_root" "$result_file"
  ); then
    status=0
  else
    status=$?
  fi
  [ ! -s "$result_file" ] || cp "$result_file" "$last_forward_result"
  [ "$status" -ne 0 ] || cat "$result_file"
  return "$status"
}

# HTTP 200 is accepted only after an authoritative exact-candidate read.
reset_incumbent
[ "$(forward)" = http-200-exact-candidate ]
[ "$(<"$cas_count_file")" -eq 1 ]
[ "$(<"$apply_count_file")" -eq 1 ]
cmp -s "$state_spec" "$candidate_spec"
echo 'forward case HTTP 200 exact candidate: PASS'

# A third Spec landing immediately before forward CAS causes an Engine 409. The
# transaction must preserve it and perform zero candidate overwrites.
reset_incumbent
cas_mode=conflict
set +e
forward >/dev/null 2>&1
status=$?
set -e
[ "$status" -ne 0 ]
[ "$(<"$cas_count_file")" -eq 1 ]
[ "$(<"$apply_count_file")" -eq 0 ]
cmp -s "$state_spec" "$third_spec"
[ "$(<"$last_forward_result")" = unknown-third-preserved ]
echo 'forward case pre-submit third Spec yields 409 and zero overwrite: PASS'

# A 409 can also mean another request already established the byte-exact desired
# candidate. Exact reconciliation accepts it without a duplicate request.
reset_incumbent
cas_mode=conflict_exact_candidate
[ "$(forward)" = http-409-exact-candidate ]
[ "$(<"$cas_count_file")" -eq 1 ]
[ "$(<"$apply_count_file")" -eq 0 ]
cmp -s "$state_spec" "$candidate_spec"
echo 'forward case HTTP 409 exact candidate reconciliation: PASS'

# A response lost before acceptance receives one bounded retry only while the
# byte-exact incumbent and original Version.Index remain authoritative.
reset_incumbent
cas_mode=lost_without_apply_then_200
[ "$(forward)" = lost-before-acceptance-bounded-retry-exact-candidate ]
[ "$(<"$cas_count_file")" -eq 2 ]
[ "$(<"$apply_count_file")" -eq 1 ]
cmp -s "$state_spec" "$candidate_spec"
echo 'forward case lost before acceptance bounded retry: PASS'

# A response lost after acceptance reconciles the exact candidate and never
# submits a duplicate.
reset_incumbent
cas_mode=apply_lost
[ "$(forward)" = lost-after-acceptance-exact-candidate ]
[ "$(<"$cas_count_file")" -eq 1 ]
[ "$(<"$apply_count_file")" -eq 1 ]
cmp -s "$state_spec" "$candidate_spec"
echo 'forward case lost after acceptance no duplicate: PASS'

# If candidate capture fails after acceptance, forward apply is RED and the
# armed recovery path must restore and verify the incumbent rather than leave an
# unverified candidate silently live.
reset_incumbent
capture_fail_on=2
set +e
forward >/dev/null 2>&1
status=$?
set -e
[ "$status" -ne 0 ]
cmp -s "$state_spec" "$candidate_spec"
[ "$(<"$last_forward_result")" = candidate-capture-failed-after-submit ]
capture_fail_on=0
recover
cmp -s "$state_spec" "$incumbent_spec"
echo 'forward case candidate capture failure recovers incumbent: PASS'

run_failed_gate() {
  local gate_name=$1 gate_status=$2 recovery_status
  [ "$gate_status" -ne 0 ]
  if recover; then
    recovery_status=0
  else
    recovery_status=$?
  fi
  [ "$recovery_status" -eq 0 ] || return 97
  printf '%s\n' "$gate_name recovered exact incumbent" >&2
  return "$gate_status"
}

# A third writer lands between classification and the confirming read. Recovery
# must not issue a CAS against it.
reset_candidate
transition_on_capture=2
set +e
recover
status=$?
set -e
[ "$status" -ne 0 ]
[ "$(<"$cas_count_file")" -eq 0 ]
cmp -s "$state_spec" "$third_spec"
echo 'recovery case concurrent third-Spec race: PASS'

# A readiness gate fails after candidate capture. The deploy remains failed,
# while the exact incumbent is restored and verified.
reset_candidate
set +e
run_failed_gate readiness 31 >/dev/null 2>&1
status=$?
set -e
[ "$status" -eq 31 ]
[ "$(<"$cas_count_file")" -eq 1 ]
cmp -s "$state_spec" "$incumbent_spec"
echo 'recovery case readiness failure: PASS'

# A later public canary failure follows the same external-gate transaction.
reset_candidate
set +e
run_failed_gate canary 32 >/dev/null 2>&1
status=$?
set -e
[ "$status" -eq 32 ]
[ "$(<"$cas_count_file")" -eq 1 ]
cmp -s "$state_spec" "$incumbent_spec"
echo 'recovery case canary failure: PASS'

# A response can be lost after the Engine accepted the CAS. Reconciliation must
# accept the exact incumbent without submitting a duplicate mutation.
reset_candidate
cas_mode=apply_lost
recover
[ "$(<"$cas_count_file")" -eq 1 ]
cmp -s "$state_spec" "$incumbent_spec"
echo 'recovery case lost response after acceptance: PASS'

# A response lost before application receives one bounded retry while the exact
# candidate version remains authoritative.
reset_candidate
cas_mode=lost_without_apply_then_200
recover
[ "$(<"$cas_count_file")" -eq 2 ]
cmp -s "$state_spec" "$incumbent_spec"
echo 'recovery case bounded lost-response retry: PASS'

# A CAS conflict that exposes a concurrent third Spec must remain untouched.
reset_candidate
cas_mode=conflict
set +e
recover >/dev/null 2>&1
status=$?
set -e
[ "$status" -ne 0 ]
[ "$(<"$cas_count_file")" -eq 1 ]
cmp -s "$state_spec" "$third_spec"
echo 'recovery case CAS conflict preserves third Spec: PASS'

# Restoring bytes is insufficient: the runtime verifier must also pass.
reset_candidate
verify_mode=fail
set +e
recover
status=$?
set -e
[ "$status" -ne 0 ]
cmp -s "$state_spec" "$incumbent_spec"
echo 'recovery case rollback verification failure is RED: PASS'

echo 'staging gateway Spec transaction tests: PASS (13 adversarial cases)'
