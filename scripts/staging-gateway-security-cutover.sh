#!/usr/bin/env bash

# Security-cutover-only forward transaction and runtime proof. The normal
# staging transaction remains in staging-gateway-spec-recovery.sh unchanged.

if ! declare -F staging_gateway_canonicalize_spec_file >/dev/null; then
  _staging_gateway_script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
  # shellcheck source=staging-gateway-spec-canonicalization.sh disable=SC1091
  source "$_staging_gateway_script_root/staging-gateway-spec-canonicalization.sh"
  unset _staging_gateway_script_root
fi

staging_gateway_security_cutover_write_result() {
  local destination=$1 result=$2
  case "$result" in
    security-cutover-submitted-pending|\
    security-cutover-http-200-exact-candidate|\
    security-cutover-cas-rejected-fix-forward|\
    security-cutover-ambiguous-fix-forward|\
    security-cutover-exact-state-unavailable|\
    security-cutover-unknown-state-fix-forward|\
    security-cutover-interrupted-fix-forward) ;;
    *)
      echo 'RED: refused unknown security-cutover transaction result' >&2
      return 1
      ;;
  esac
  local temporary="${destination}.tmp"
  (umask 077; printf '%s\n' "$result" > "$temporary")
  mv -f -- "$temporary" "$destination"
}

staging_gateway_security_cutover_exact_state() {
  local observed_spec=$1 observed_id=$2 expected_spec=$3 expected_id=$4
  [ -s "$observed_spec" ] && [ -s "$observed_id" ] \
    && [ -s "$expected_spec" ] && [ -s "$expected_id" ] \
    && staging_gateway_specs_equal "$observed_spec" "$expected_spec" \
    && cmp -s "$observed_id" "$expected_id"
}

staging_gateway_security_cutover_forward_apply() {
  local incumbent_spec=$1 incumbent_version=$2 incumbent_id=$3
  local candidate_spec=$4 candidate_version=$5 candidate_id=$6
  local transaction_root=$7 result_file=$8
  local observed_spec observed_version observed_id
  local service_id version_index cas_status
  local callback

  for callback in capture_remote_spec staging_gateway_lock_assert \
    staging_gateway_submit_spec_cas; do
    declare -F "$callback" >/dev/null || {
      echo 'RED: security-cutover callback contract is incomplete' >&2
      return 1
    }
  done
  [ -d "$transaction_root" ] || return 1
  for required in "$incumbent_spec" "$incumbent_version" "$incumbent_id" \
    "$candidate_spec"; do
    [ -s "$required" ] || {
      echo 'RED: security-cutover input is incomplete; no mutation attempted' >&2
      return 1
    }
  done
  staging_gateway_canonicalize_spec_file "$incumbent_spec" || return 1
  staging_gateway_canonicalize_spec_file "$candidate_spec" || return 1
  staging_gateway_specs_equal "$incumbent_spec" "$candidate_spec" && {
    echo 'RED: security-cutover candidate equals incumbent; no mutation attempted' >&2
    return 1
  }

  service_id=$(<"$incumbent_id")
  version_index=$(<"$incumbent_version")
  [[ "$service_id" =~ ^[a-z0-9]+$ ]]
  [[ "$version_index" =~ ^[0-9]+$ ]]
  (umask 077; printf '%s\n' "$service_id" > "$candidate_id")
  (umask 077; : > "$candidate_version")
  observed_spec="$transaction_root/security-cutover-observed-spec.json"
  observed_version="$transaction_root/security-cutover-observed-version"
  observed_id="$transaction_root/security-cutover-observed-id"

  staging_gateway_lock_assert || return 1
  capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" || {
    echo 'RED: security-cutover authoritative pre-submit state is unavailable' >&2
    return 1
  }
  if ! staging_gateway_security_cutover_exact_state \
      "$observed_spec" "$observed_id" "$incumbent_spec" "$incumbent_id" \
    || ! cmp -s "$observed_version" "$incumbent_version"; then
    echo 'RED: security-cutover incumbent drifted; no mutation attempted' >&2
    return 1
  fi

  staging_gateway_lock_assert || return 1
  staging_gateway_security_cutover_write_result \
    "$result_file" security-cutover-submitted-pending
  cas_status=$(staging_gateway_submit_spec_cas \
    "$service_id" "$version_index" "$candidate_spec") || cas_status=''
  case "$cas_status" in
    200) ;;
    409)
      staging_gateway_security_cutover_write_result \
        "$result_file" security-cutover-cas-rejected-fix-forward
      echo 'RED: security-cutover CAS was rejected; traffic remains frozen' >&2
      return 1
      ;;
    ''|000)
      staging_gateway_security_cutover_write_result \
        "$result_file" security-cutover-ambiguous-fix-forward
      echo 'RED: security-cutover CAS outcome is ambiguous; traffic remains frozen' >&2
      return 1
      ;;
    *)
      staging_gateway_security_cutover_write_result \
        "$result_file" security-cutover-unknown-state-fix-forward
      echo 'RED: security-cutover CAS returned an unexpected status; traffic remains frozen' >&2
      return 1
      ;;
  esac

  capture_remote_spec "$observed_spec" "$observed_version" "$observed_id" || {
    staging_gateway_security_cutover_write_result \
      "$result_file" security-cutover-exact-state-unavailable
    echo 'RED: security-cutover candidate state is unavailable; traffic remains frozen' >&2
    return 1
  }
  if ! staging_gateway_security_cutover_exact_state \
      "$observed_spec" "$observed_id" "$candidate_spec" "$incumbent_id"; then
    staging_gateway_security_cutover_write_result \
      "$result_file" security-cutover-unknown-state-fix-forward
    echo 'RED: security-cutover did not reconcile to the exact candidate' >&2
    return 1
  fi
  cp "$observed_version" "$candidate_version"
  chmod 600 "$candidate_version"
  staging_gateway_security_cutover_write_result \
    "$result_file" security-cutover-http-200-exact-candidate
}

staging_gateway_security_cutover_remote_fail() {
  printf 'RED: security-cutover runtime proof failed (%s)\n' "$1" >&2
  return 1
}

staging_gateway_security_cutover_require_identity() {
  local service=$1 service_id=$2 image=$3
  [ "$service" = jeeb-staging-jeeb-gateway ] \
    || staging_gateway_security_cutover_remote_fail service-name
  [[ "$service_id" =~ ^[a-z0-9]+$ ]] \
    || staging_gateway_security_cutover_remote_fail service-id
  [[ "$image" =~ ^ghcr\.io/olivium-dev/jeeb-gateway@sha256:[0-9a-f]{64}$ ]] \
    || staging_gateway_security_cutover_remote_fail image
}

staging_gateway_security_cutover_service_document() {
  local service=$1 destination=$2
  local raw_document="${destination}.raw"
  docker service inspect "$service" --format '{{json .}}' > "$raw_document" 2>/dev/null \
    || { rm -f -- "$raw_document"; return 1; }
  if jq -e -S -c -s '
      if length == 1
        and (.[0] | type == "object")
        and (.[0].ID | type == "string")
        and (.[0].Spec | type == "object")
      then .[0]
      else error("service document must contain exactly one object Spec")
      end
    ' "$raw_document" > "$destination"; then
    rm -f -- "$raw_document"
    return 0
  fi
  rm -f -- "$raw_document" "$destination"
  return 1
}

staging_gateway_security_cutover_capture() {
  local service=$1 expected_service_id=$2 incumbent_image=$3
  local root before after before_spec after_spec before_id after_id
  local before_version after_version task_rows task_id task container_id container
  root=$(mktemp -d)
  chmod 700 "$root"
  STAGING_GATEWAY_CUTOVER_RUNTIME_ROOT=$root
  trap 'status=$?; rm -rf -- "$STAGING_GATEWAY_CUTOVER_RUNTIME_ROOT"; exit "$status"' EXIT
  before="$root/before.json"
  after="$root/after.json"
  task="$root/task.json"
  container="$root/container.json"

  staging_gateway_security_cutover_require_identity \
    "$service" "$expected_service_id" "$incumbent_image" || return 1
  staging_gateway_security_cutover_service_document "$service" "$before" \
    || { staging_gateway_security_cutover_remote_fail service-inspect; return 1; }
  jq -e --arg service_id "$expected_service_id" --arg image "$incumbent_image" '
    .ID == $service_id
    and .Spec.TaskTemplate.ContainerSpec.Image == $image
    and .Spec.Mode.Replicated.Replicas == 1
    and (.Version.Index | type == "number" and . >= 0)
  ' "$before" >/dev/null \
    || { staging_gateway_security_cutover_remote_fail incumbent-contract; return 1; }

  task_rows=$(docker service ps "$service" --no-trunc \
    --filter desired-state=running --format '{{.ID}}' 2>/dev/null) \
    || { staging_gateway_security_cutover_remote_fail task-list; return 1; }
  [ "$(printf '%s\n' "$task_rows" | awk 'NF { count++ } END { print count+0 }')" -eq 1 ] \
    || { staging_gateway_security_cutover_remote_fail incumbent-task-count; return 1; }
  task_id=$(printf '%s\n' "$task_rows" | awk 'NF { print; exit }')
  [[ "$task_id" =~ ^[a-z0-9]+$ ]] \
    || { staging_gateway_security_cutover_remote_fail incumbent-task-id; return 1; }
  docker inspect "$task_id" --format '{{json .}}' > "$task" 2>/dev/null \
    || { staging_gateway_security_cutover_remote_fail incumbent-task-inspect; return 1; }
  container_id=$(jq -er --arg service_id "$expected_service_id" \
    --arg image "$incumbent_image" '
      select(.ServiceID == $service_id)
      | select(.DesiredState == "running" and .Status.State == "running")
      | select(.Spec.ContainerSpec.Image == $image)
      | .Status.ContainerStatus.ContainerID
      | select(test("^[0-9a-f]{64}$"))
    ' "$task") \
    || { staging_gateway_security_cutover_remote_fail incumbent-task-contract; return 1; }
  docker container inspect "$container_id" --format '{{json .}}' > "$container" 2>/dev/null \
    || { staging_gateway_security_cutover_remote_fail incumbent-container-inspect; return 1; }
  jq -e '.State.Running == true' "$container" >/dev/null \
    || { staging_gateway_security_cutover_remote_fail incumbent-container-state; return 1; }

  staging_gateway_security_cutover_service_document "$service" "$after" \
    || { staging_gateway_security_cutover_remote_fail service-confirm-inspect; return 1; }
  before_spec="$root/before-spec.json"; after_spec="$root/after-spec.json"
  before_id=$(jq -er '.ID' "$before") \
    || { staging_gateway_security_cutover_remote_fail incumbent-capture-drift; return 1; }
  after_id=$(jq -er '.ID' "$after") \
    || { staging_gateway_security_cutover_remote_fail incumbent-capture-drift; return 1; }
  before_version=$(jq -er '.Version.Index' "$before") \
    || { staging_gateway_security_cutover_remote_fail incumbent-capture-drift; return 1; }
  after_version=$(jq -er '.Version.Index' "$after") \
    || { staging_gateway_security_cutover_remote_fail incumbent-capture-drift; return 1; }
  if ! jq -e '.Spec' "$before" > "$before_spec" \
    || ! jq -e '.Spec' "$after" > "$after_spec" \
    || [ "$before_id" != "$after_id" ] \
    || [ "$before_version" != "$after_version" ] \
    || ! staging_gateway_specs_equal "$before_spec" "$after_spec"; then
    staging_gateway_security_cutover_remote_fail incumbent-capture-drift
    return 1
  fi

  jq -cn --arg service_id "$expected_service_id" \
    --arg image "$incumbent_image" --arg task_id "$task_id" \
    --arg container_id "$container_id" \
    --argjson version "$(jq '.Version.Index' "$before")" '
      {
        ServiceID:$service_id,
        VersionIndex:$version,
        IncumbentImage:$image,
        Tasks:[{TaskID:$task_id,ContainerID:$container_id}]
      }
    '
}

staging_gateway_security_cutover_observe() {
  local service=$1 expected_service_id=$2 candidate_image=$3
  local expected_spec_sha=$4 expected_version=$5 manifest=$6 root=$7
  local service_document update_state actual_spec_sha task_rows candidate_task_id
  local candidate_task candidate_state candidate_container_id candidate_container health_state
  local old_task_id old_container_id old_task old_state old_container rows
  service_document="$root/service.json"
  candidate_task="$root/candidate-task.json"
  candidate_container="$root/candidate-container.json"

  staging_gateway_security_cutover_service_document "$service" "$service_document" \
    || return 40
  jq -e --arg service_id "$expected_service_id" --arg image "$candidate_image" \
    --argjson version "$expected_version" '
      .ID == $service_id
      and .Version.Index == $version
      and .Spec.TaskTemplate.ContainerSpec.Image == $image
      and .Spec.Mode.Replicated.Replicas == 1
      and .Spec.UpdateConfig.Parallelism == 1
      and .Spec.UpdateConfig.Monitor == 20000000000
      and .Spec.UpdateConfig.Order == "stop-first"
      and .Spec.UpdateConfig.FailureAction == "pause"
    ' "$service_document" >/dev/null || return 41
  actual_spec_sha=$(jq -e -S -c -s '
    if length == 1 and (.[0].Spec | type) == "object" then .[0].Spec
    else error("service document must contain exactly one object Spec") end
  ' "$service_document" | sha256sum | awk '{print $1}')
  [ "$actual_spec_sha" = "$expected_spec_sha" ] || return 41
  update_state=$(jq -er '.UpdateStatus.State // ""' "$service_document") || return 40
  case "$update_state" in
    completed) ;;
    updating) return 3 ;;
    paused|rollback_started|rollback_paused|rollback_completed) return 42 ;;
    *) return 41 ;;
  esac

  while IFS=$'\t' read -r old_task_id old_container_id; do
    [[ "$old_task_id" =~ ^[a-z0-9]+$ ]] && [[ "$old_container_id" =~ ^[0-9a-f]{64}$ ]] \
      || return 41
    old_task="$root/old-task-${old_task_id}.json"
    docker inspect "$old_task_id" --format '{{json .}}' > "$old_task" 2>/dev/null \
      || return 40
    old_state=$(jq -er --arg service_id "$expected_service_id" '
      select(.ServiceID == $service_id)
      | select(.DesiredState == "shutdown")
      | .Status.State
    ' "$old_task") || return 41
    case "$old_state" in
      complete|shutdown|failed|rejected|orphaned|remove) ;;
      new|allocated|pending|assigned|accepted|preparing|ready|starting|running) return 3 ;;
      *) return 41 ;;
    esac
    old_container="$root/old-container-${old_container_id}.json"
    if docker container inspect "$old_container_id" --format '{{json .}}' \
        > "$old_container" 2>/dev/null; then
      jq -e '.State.Running == false' "$old_container" >/dev/null || return 3
    else
      rows=$(docker container ls --all --no-trunc --filter "id=$old_container_id" \
        --format '{{.ID}}' 2>/dev/null) || return 40
      [ -z "$rows" ] || return 40
    fi
  done < <(jq -r '.Tasks[] | [.TaskID,.ContainerID] | @tsv' "$manifest")

  task_rows=$(docker service ps "$service" --no-trunc \
    --filter desired-state=running --format '{{.ID}}' 2>/dev/null) || return 40
  rows=$(printf '%s\n' "$task_rows" | awk 'NF { count++ } END { print count+0 }')
  [ "$rows" -le 1 ] || return 41
  [ "$rows" -eq 1 ] || return 3
  candidate_task_id=$(printf '%s\n' "$task_rows" | awk 'NF { print; exit }')
  [[ "$candidate_task_id" =~ ^[a-z0-9]+$ ]] || return 41
  docker inspect "$candidate_task_id" --format '{{json .}}' > "$candidate_task" 2>/dev/null \
    || return 40
  jq -e --arg service_id "$expected_service_id" --arg image "$candidate_image" '
    .ServiceID == $service_id
    and .DesiredState == "running"
    and .Spec.ContainerSpec.Image == $image
  ' "$candidate_task" >/dev/null || return 41
  candidate_state=$(jq -er '.Status.State' "$candidate_task") || return 40
  case "$candidate_state" in
    running) ;;
    new|allocated|pending|assigned|accepted|preparing|ready|starting) return 3 ;;
    complete|shutdown|failed|rejected|orphaned|remove) return 41 ;;
    *) return 41 ;;
  esac
  candidate_container_id=$(jq -er --arg service_id "$expected_service_id" \
    --arg image "$candidate_image" '
      select(.ServiceID == $service_id)
      | select(.DesiredState == "running")
      | select(.Spec.ContainerSpec.Image == $image)
      | .Status.ContainerStatus.ContainerID
      | select(test("^[0-9a-f]{64}$"))
    ' "$candidate_task") || return 41
  docker container inspect "$candidate_container_id" --format '{{json .}}' \
    > "$candidate_container" 2>/dev/null || return 40
  jq -e '.State.Running == true' "$candidate_container" >/dev/null || return 3
  health_state=$(jq -er '.State.Health.Status // ""' "$candidate_container") || return 40
  case "$health_state" in
    healthy) ;;
    starting) return 3 ;;
    unhealthy) return 41 ;;
    *) return 41 ;;
  esac

  jq -cn --arg service_id "$expected_service_id" --argjson version "$expected_version" \
    --arg spec_sha "$expected_spec_sha" --arg task_id "$candidate_task_id" \
    --arg container_id "$candidate_container_id" \
    --argjson old "$(jq -c '.Tasks' "$manifest")" '
      {
        ServiceID:$service_id,
        VersionIndex:$version,
        SpecSha256:$spec_sha,
        CandidateTaskID:$task_id,
        CandidateContainerID:$container_id,
        OldTasks:$old
      }
    '
}

staging_gateway_security_cutover_verify() {
  local service=$1 expected_service_id=$2 candidate_image=$3
  local expected_spec_sha=$4 expected_version=$5 manifest_base64=$6
  local root manifest max_attempts delay attempt first second status
  root=$(mktemp -d)
  chmod 700 "$root"
  STAGING_GATEWAY_CUTOVER_RUNTIME_ROOT=$root
  trap 'status=$?; rm -rf -- "$STAGING_GATEWAY_CUTOVER_RUNTIME_ROOT"; exit "$status"' EXIT
  manifest="$root/incumbent-tasks.json"

  staging_gateway_security_cutover_require_identity \
    "$service" "$expected_service_id" "$candidate_image" || return 1
  [[ "$expected_spec_sha" =~ ^[0-9a-f]{64}$ ]] \
    || { staging_gateway_security_cutover_remote_fail spec-sha; return 1; }
  [[ "$expected_version" =~ ^[0-9]+$ ]] \
    || { staging_gateway_security_cutover_remote_fail version; return 1; }
  printf '%s' "$manifest_base64" | base64 --decode > "$manifest" 2>/dev/null \
    || { staging_gateway_security_cutover_remote_fail manifest-encoding; return 1; }
  chmod 600 "$manifest"
  jq -e --arg service_id "$expected_service_id" '
    type == "object"
    and keys == ["IncumbentImage", "ServiceID", "Tasks", "VersionIndex"]
    and .ServiceID == $service_id
    and (.VersionIndex | type == "number" and . >= 0)
    and (.IncumbentImage | test("^ghcr\\.io/olivium-dev/jeeb-gateway@sha256:[0-9a-f]{64}$"))
    and (.Tasks | length == 1)
    and (.Tasks[0] | keys == ["ContainerID", "TaskID"])
    and (.Tasks[0].TaskID | test("^[a-z0-9]+$"))
    and (.Tasks[0].ContainerID | test("^[0-9a-f]{64}$"))
  ' "$manifest" >/dev/null \
    || { staging_gateway_security_cutover_remote_fail manifest-contract; return 1; }

  max_attempts=${STAGING_GATEWAY_CUTOVER_MAX_ATTEMPTS:-20}
  delay=${STAGING_GATEWAY_CUTOVER_POLL_SECONDS:-3}
  [[ "$max_attempts" =~ ^([1-9]|1[0-9]|20)$ ]] \
    || { staging_gateway_security_cutover_remote_fail attempt-bound; return 1; }
  [[ "$delay" =~ ^[0-5]$ ]] \
    || { staging_gateway_security_cutover_remote_fail delay-bound; return 1; }

  for attempt in $(seq 1 "$max_attempts"); do
    : "$attempt"
    set +e
    first=$(staging_gateway_security_cutover_observe \
      "$service" "$expected_service_id" "$candidate_image" \
      "$expected_spec_sha" "$expected_version" "$manifest" "$root")
    status=$?
    set -e
    case "$status" in
      0)
        sleep "$delay"
        set +e
        second=$(staging_gateway_security_cutover_observe \
          "$service" "$expected_service_id" "$candidate_image" \
          "$expected_spec_sha" "$expected_version" "$manifest" "$root")
        status=$?
        set -e
        [ "$status" -eq 0 ] && [ "$first" = "$second" ] || {
          staging_gateway_security_cutover_remote_fail unstable-proof
          return 1
        }
        printf '%s\n' 'Security-cutover runtime proof passed (old task terminal; one exact healthy candidate; two stable reads).'
        return 0
        ;;
      3) sleep "$delay" ;;
      42)
        staging_gateway_security_cutover_remote_fail update-paused
        return 1
        ;;
      40)
        staging_gateway_security_cutover_remote_fail inspect-error
        return 1
        ;;
      *)
        staging_gateway_security_cutover_remote_fail ambiguous-state
        return 1
        ;;
    esac
  done
  staging_gateway_security_cutover_remote_fail timeout
}

staging_gateway_security_cutover_remote_main() {
  local operation=${1:-}
  shift || true
  case "$operation" in
    capture)
      [ "$#" -eq 3 ] || return 64
      staging_gateway_security_cutover_capture "$@"
      ;;
    verify)
      [ "$#" -eq 6 ] || return 64
      staging_gateway_security_cutover_verify "$@"
      ;;
    *) return 64 ;;
  esac
}

if [ "${1:-}" = --execute ]; then
  shift
  set -euo pipefail
  staging_gateway_security_cutover_remote_main "$@"
fi
