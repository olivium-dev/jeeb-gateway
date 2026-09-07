#!/usr/bin/env bash
# SOURCE DRAFT ONLY. No workflow or default adapters: missing custody, readiness,
# durable journal, or authority integration must stop before either submission.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/staging-gateway-security-cutover.sh"

staging_delivery_paired_activation_draft() {
  local root=$1 callback role
  [ -d "$root" ] && [ ! -L "$root" ] || return 1
  # Each adapter is mandatory. There is no success default or production fallback.
  for callback in staging_gateway_lock_assert \
    paired_require_current_protected_builds paired_require_exact_daemon \
    paired_require_existing_credential paired_validate_narrow_candidates \
    paired_probe_delivery_schema paired_journal_begin paired_journal_advance \
    paired_capture_role paired_submit_role_cas paired_verify_gateway \
    paired_verify_delivery paired_verify_authenticated_wire; do
    declare -F "$callback" >/dev/null || {
      echo 'Paired activation draft integration incomplete; no submission authorized.' >&2
      return 1
    }
  done
  staging_gateway_lock_assert || return 1
  paired_require_current_protected_builds "$root" || return 1
  paired_require_exact_daemon || return 1
  paired_require_existing_credential "$root" || return 1
  paired_validate_narrow_candidates "$root" || return 1
  paired_probe_delivery_schema "$root" || return 1
  # Durable, exclusive begin must reject any existing activation history rather
  # than treating absent current declarations as permission to activate again.
  paired_journal_begin "$root" || return 1

  # Reuse the existing no-rollback forward transaction, not spec recovery.
  # Explicit fixed-role adapters prevent arbitrary service targets.
  capture_remote_spec() {
    paired_capture_role "$PAIRED_ACTIVATION_ROLE" "$@"
  }
  staging_gateway_submit_spec_cas() {
    paired_submit_role_cas "$PAIRED_ACTIVATION_ROLE" "$@"
  }
  for role in gateway delivery; do
    PAIRED_ACTIVATION_ROLE=$role
    staging_gateway_lock_assert || return 1
    paired_require_current_protected_builds "$root" || return 1
    paired_require_exact_daemon || return 1
    # Persist pending BEFORE POST. Failure, interruption, or lost acknowledgement
    # leaves the durable phase pending and cannot authorize automatic re-entry.
    paired_journal_advance "$role-submission-pending" "$root" || return 1
    staging_gateway_security_cutover_forward_apply \
      "$root/$role-incumbent.json" "$root/$role-incumbent-version" \
      "$root/$role-incumbent-id" "$root/$role-candidate.json" \
      "$root/$role-candidate-version" "$root/$role-candidate-id" \
      "$root" "$root/$role-result" || return 1
    if [ "$role" = gateway ]; then
      # Actual loaded file format/UID and gateway runtime proof, not the
      # inactive delivery endpoint or a conditionally skipped readiness check.
      paired_verify_gateway "$root" || return 1
    else
      paired_verify_delivery "$root" || return 1
      paired_verify_authenticated_wire "$root" || return 1
    fi
    paired_journal_advance "$role-verified" "$root" || return 1
  done
  staging_gateway_lock_assert || return 1
  paired_journal_advance complete "$root" || return 1
  echo 'Paired activation transaction verified.'
}
