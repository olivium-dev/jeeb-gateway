#!/usr/bin/env bash

# Source from a strict Bash shell after defining these workflow-owned callbacks:
#
#   capture_remote_spec SPEC_FILE VERSION_FILE ID_FILE
#   staging_gateway_lock_assert
#   staging_gateway_submit_spec_cas SERVICE_ID VERSION SPEC_FILE
#   staging_gateway_verify_incumbent SPEC_FILE VERSION_FILE ID_FILE
#
# Forward apply and recovery send complete, prevalidated Specs through Docker
# Engine version-CAS requests. They never select a mutable image/tag and never
# overwrite an unrecognised concurrent Spec.

_staging_gateway_script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=staging-gateway-spec-canonicalization.sh disable=SC1091
source "$_staging_gateway_script_root/staging-gateway-spec-canonicalization.sh"
unset _staging_gateway_script_root

staging_gateway_require_recovery_callbacks() {
  local callback
  for callback in capture_remote_spec staging_gateway_lock_assert \
    staging_gateway_submit_spec_cas staging_gateway_verify_incumbent; do
    declare -F "$callback" >/dev/null || {
      echo "RED: required recovery callback is missing: $callback" >&2
      return 1
    }
  done
}

staging_gateway_exact_state() {
  local observed_spec=$1 observed_id=$2 expected_spec=$3 expected_id=$4
  [ -s "$observed_spec" ] && [ -s "$observed_id" ] \
    && [ -s "$expected_spec" ] && [ -s "$expected_id" ] \
    && staging_gateway_specs_equal "$observed_spec" "$expected_spec" \
    && cmp -s "$observed_id" "$expected_id"
}

staging_gateway_write_sanitized_result() {
  local destination=$1 result=$2
  case "$result" in
    submitted-pending-reconciliation|\
    http-200-exact-candidate|http-409-exact-candidate|\
    lost-after-acceptance-exact-candidate|\
    lost-before-acceptance-bounded-retry-exact-candidate|\
    unknown-third-preserved|candidate-capture-failed-after-submit|\
    unexpected-http-status-after-submit|\
    http-200-exact-incumbent-invalid|http-409-exact-incumbent-no-retry|\
    lock-lost-before-bounded-retry|\
    unexpected-http-status-after-bounded-retry|\
    candidate-capture-failed-after-bounded-retry|\
    bounded-retry-exact-incumbent-no-candidate|\
    bounded-retry-unreconciled|\
    submission-interrupted-recovered-incumbent|\
    submission-interrupted-recovery-failed|\
    exact-incumbent-already|exact-incumbent-recovered) ;;
    *)
      echo 'RED: refused to persist an unknown transaction result' >&2
      return 1
      ;;
  esac
  local temporary="${destination}.tmp"
  (umask 077; printf '%s\n' "$result" > "$temporary")
  mv -f -- "$temporary" "$destination"
}

staging_gateway_forward_apply() {
  local incumbent_spec=$1 incumbent_version=$2 incumbent_id=$3
  local candidate_spec=$4 candidate_version=$5 candidate_id=$6
  local transaction_root=$7 result_file=$8
  local observed_spec observed_version observed_id
  local cas_status incumbent_service_id incumbent_index result

  staging_gateway_require_recovery_callbacks || return 1
  [ -d "$transaction_root" ] || return 1
  for required in "$incumbent_spec" "$incumbent_version" "$incumbent_id" \
    "$candidate_spec"; do
    [ -s "$required" ] || {
      echo 'RED: forward transaction input is incomplete; no mutation attempted' >&2
      return 1
    }
  done
  staging_gateway_canonicalize_spec_file "$incumbent_spec" || return 1
  staging_gateway_canonicalize_spec_file "$candidate_spec" || return 1
  staging_gateway_specs_equal "$incumbent_spec" "$candidate_spec" && {
    echo 'RED: desired candidate equals the incumbent; no mutation attempted' >&2
    return 1
  }

  incumbent_service_id=$(<"$incumbent_id")
  incumbent_index=$(<"$incumbent_version")
  [[ "$incumbent_service_id" =~ ^[a-z0-9]+$ ]]
  [[ "$incumbent_index" =~ ^[0-9]+$ ]]
  (umask 077; printf '%s\n' "$incumbent_service_id" > "$candidate_id")
  (umask 077; : > "$candidate_version")

  observed_spec="$transaction_root/forward-observed-spec.json"
  observed_version="$transaction_root/forward-observed-version"
  observed_id="$transaction_root/forward-observed-id"

  staging_gateway_lock_assert || return 1
  capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" || {
    echo 'RED: authoritative pre-submit state is unavailable; no mutation attempted' >&2
    return 1
  }
  if ! staging_gateway_exact_state \
      "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id" \
    || ! cmp -s "$observed_version" "$incumbent_version"; then
    echo 'RED: incumbent changed before forward submission; no mutation attempted' >&2
    return 1
  fi

  staging_gateway_lock_assert || return 1
  staging_gateway_write_sanitized_result \
    "$result_file" submitted-pending-reconciliation
  cas_status=$(staging_gateway_submit_spec_cas \
    "$incumbent_service_id" "$incumbent_index" "$candidate_spec") || cas_status=''
  case "$cas_status" in
    200|409) ;;
    ''|000) ;;
    *)
      staging_gateway_write_sanitized_result \
        "$result_file" unexpected-http-status-after-submit
      echo "RED: forward CAS returned unexpected HTTP status: $cas_status" >&2
      return 1
      ;;
  esac

  capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" || {
    staging_gateway_write_sanitized_result \
      "$result_file" candidate-capture-failed-after-submit
    echo 'RED: forward CAS outcome is ambiguous and cannot be reconciled' >&2
    return 1
  }
  if staging_gateway_exact_state \
    "$observed_spec" "$observed_id" "$candidate_spec" "$incumbent_id"; then
    case "$cas_status" in
      200) result=http-200-exact-candidate ;;
      409) result=http-409-exact-candidate ;;
      ''|000) result=lost-after-acceptance-exact-candidate ;;
    esac
    cp "$observed_version" "$candidate_version"
    chmod 600 "$candidate_version"
    staging_gateway_write_sanitized_result "$result_file" "$result"
    return 0
  fi

  if ! staging_gateway_exact_state \
      "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id" \
    || ! cmp -s "$observed_version" "$incumbent_version"; then
    staging_gateway_write_sanitized_result \
      "$result_file" unknown-third-preserved
    echo 'RED: forward outcome is an unknown third Spec; no overwrite attempted' >&2
    return 1
  fi
  if [ -n "$cas_status" ] && [ "$cas_status" != 000 ]; then
    case "$cas_status" in
      200) result=http-200-exact-incumbent-invalid ;;
      409) result=http-409-exact-incumbent-no-retry ;;
    esac
    staging_gateway_write_sanitized_result "$result_file" "$result"
    echo "RED: forward HTTP $cas_status left the exact incumbent; no retry attempted" >&2
    return 1
  fi

  # One retry is allowed only for a lost response while the exact incumbent at
  # the original Version.Index remains authoritative.
  staging_gateway_lock_assert || {
    staging_gateway_write_sanitized_result \
      "$result_file" lock-lost-before-bounded-retry
    return 1
  }
  cas_status=$(staging_gateway_submit_spec_cas \
    "$incumbent_service_id" "$incumbent_index" "$candidate_spec") || cas_status=''
  case "$cas_status" in
    200|409|''|000) ;;
    *)
      staging_gateway_write_sanitized_result \
        "$result_file" unexpected-http-status-after-bounded-retry
      return 1
      ;;
  esac
  capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" || {
    staging_gateway_write_sanitized_result \
      "$result_file" candidate-capture-failed-after-bounded-retry
    echo 'RED: bounded forward retry cannot be reconciled' >&2
    return 1
  }
  if staging_gateway_exact_state \
    "$observed_spec" "$observed_id" "$candidate_spec" "$incumbent_id"; then
    cp "$observed_version" "$candidate_version"
    chmod 600 "$candidate_version"
    staging_gateway_write_sanitized_result \
      "$result_file" lost-before-acceptance-bounded-retry-exact-candidate
    return 0
  fi

  if staging_gateway_exact_state \
      "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id" \
    && cmp -s "$observed_version" "$incumbent_version"; then
    staging_gateway_write_sanitized_result \
      "$result_file" bounded-retry-exact-incumbent-no-candidate
  elif staging_gateway_exact_state \
    "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id"; then
    staging_gateway_write_sanitized_result \
      "$result_file" bounded-retry-unreconciled
  else
    staging_gateway_write_sanitized_result \
      "$result_file" unknown-third-preserved
  fi

  echo 'RED: bounded forward retry did not reconcile to the exact candidate' >&2
  return 1
}

staging_gateway_external_gate_recover() {
  local incumbent_spec=$1 incumbent_version=$2 incumbent_id=$3
  local candidate_spec=$4 candidate_version=$5 candidate_id=$6
  local recovery_root=$7
  local observed_spec observed_version observed_id
  local confirm_spec confirm_version confirm_id
  local cas_status candidate_service_id candidate_index retry_allowed=false

  staging_gateway_require_recovery_callbacks || return 1
  [ -d "$recovery_root" ] || return 1
  for required in "$incumbent_spec" "$incumbent_version" "$incumbent_id" \
    "$candidate_spec" "$candidate_id"; do
    [ -s "$required" ] || {
      echo 'RED: recovery input snapshot is incomplete; no mutation attempted' >&2
      return 1
    }
  done
  staging_gateway_canonicalize_spec_file "$incumbent_spec" || return 1
  staging_gateway_canonicalize_spec_file "$candidate_spec" || return 1

  observed_spec="$recovery_root/recovery-observed-spec.json"
  observed_version="$recovery_root/recovery-observed-version"
  observed_id="$recovery_root/recovery-observed-id"
  confirm_spec="$recovery_root/recovery-confirm-spec.json"
  confirm_version="$recovery_root/recovery-confirm-version"
  confirm_id="$recovery_root/recovery-confirm-id"

  staging_gateway_lock_assert || return 1
  capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" || {
    echo 'RED: authoritative recovery state is unavailable; no mutation attempted' >&2
    return 1
  }

  if staging_gateway_exact_state \
    "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id"; then
    if staging_gateway_verify_incumbent \
      "$observed_spec" "$observed_version" "$observed_id"; then
      return 0
    fi
    echo 'RED: exact incumbent Spec failed runtime verification' >&2
    return 1
  fi

  if ! staging_gateway_exact_state \
      "$observed_spec" "$observed_id" "$candidate_spec" "$candidate_id"; then
    echo 'RED: live state is neither the exact incumbent nor the exact candidate; recovery refused' >&2
    return 1
  fi

  # A second stable read closes the gap between classification and CAS.
  capture_remote_spec "$confirm_spec" "$confirm_version" "$confirm_id" || return 1
  if ! staging_gateway_specs_equal "$confirm_spec" "$candidate_spec" \
    || ! cmp -s "$confirm_version" "$observed_version" \
    || ! cmp -s "$confirm_id" "$candidate_id"; then
    echo 'RED: candidate changed before recovery CAS; recovery refused' >&2
    return 1
  fi

  candidate_service_id=$(<"$candidate_id")
  candidate_index=$(<"$observed_version")
  [[ "$candidate_service_id" =~ ^[a-z0-9]+$ ]]
  [[ "$candidate_index" =~ ^[0-9]+$ ]]
  staging_gateway_lock_assert || return 1
  cas_status=$(staging_gateway_submit_spec_cas \
    "$candidate_service_id" "$candidate_index" "$incumbent_spec") || cas_status=''
  case "$cas_status" in
    200|409) ;;
    ''|000) retry_allowed=true ;;
    *)
      echo "RED: recovery CAS returned unexpected HTTP status: $cas_status" >&2
      return 1
      ;;
  esac

  capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" || {
    echo 'RED: recovery CAS outcome is ambiguous and cannot be reconciled' >&2
    return 1
  }
  if staging_gateway_exact_state \
    "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id"; then
    if staging_gateway_verify_incumbent \
      "$observed_spec" "$observed_version" "$observed_id"; then
      return 0
    fi
    echo 'RED: recovered incumbent failed runtime verification' >&2
    return 1
  fi

  # Only a lost-response outcome may be retried, and only while the exact same
  # candidate and Version.Index are still authoritative. The version CAS makes
  # concurrent acceptance of either request safe; reconciliation remains exact.
  if [ "$retry_allowed" = true ] \
    && staging_gateway_exact_state \
      "$observed_spec" "$observed_id" "$candidate_spec" "$candidate_id" \
    && [ "$(<"$observed_version")" = "$candidate_index" ]; then
    staging_gateway_lock_assert || return 1
    cas_status=$(staging_gateway_submit_spec_cas \
      "$candidate_service_id" "$candidate_index" "$incumbent_spec") || cas_status=''
    case "$cas_status" in 200|409|''|000) ;; *) return 1 ;; esac
    capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" \
      || return 1
    if staging_gateway_exact_state \
      "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id"; then
      if staging_gateway_verify_incumbent \
        "$observed_spec" "$observed_version" "$observed_id"; then
        return 0
      fi
      echo 'RED: retried recovery failed runtime verification' >&2
      return 1
    fi
  fi

  echo 'RED: recovery did not reconcile to the exact incumbent; no further mutation attempted' >&2
  return 1
}
