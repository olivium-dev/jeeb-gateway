#!/usr/bin/env bash
set -euo pipefail

readonly SECRET_TARGET=/app/appsettings.Production.json
readonly MAX_WAIT_ATTEMPTS=60

fail() {
  echo "gateway secret lifecycle error: $1" >&2
  exit 1
}

is_managed_secret() {
  [[ "$1" =~ ^jeeb_gateway_appsettings_[0-9]+_[0-9]+$ ]]
}

service_exists() {
  docker service inspect "$1" >/dev/null 2>&1
}

current_image() {
  docker service inspect --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}' "$1"
}

assert_exact_running_image() {
  local service_name=$1
  local expected_image=$2
  local service_id
  local service_image
  local desired
  local task_ids
  local task_id
  local task_state
  local expected_image_id
  local task_image
  local container_id
  local actual_image_id

  [[ "$expected_image" =~ ^[^[:space:]]+@sha256:[0-9a-f]{64}$ ]] \
    || fail "expected service image is not digest-pinned"
  service_id=$(docker service inspect "$service_name" --format '{{.ID}}')
  [[ "$service_id" =~ ^[A-Za-z0-9]+$ ]] || fail "service has no immutable ID"
  service_image=$(docker service inspect "$service_id" \
    --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}')
  [[ "$service_image" == "$expected_image" ]] || fail "service image changed during restart"
  desired=$(docker service inspect "$service_id" --format '{{.Spec.Mode.Replicated.Replicas}}')
  [[ "$desired" == 1 ]] || fail "service must have exactly one desired replica"
  task_ids=$(docker service ps "$service_id" --filter desired-state=running --format '{{.ID}}')
  [[ "$(printf '%s\n' "$task_ids" | sed '/^$/d' | wc -l | tr -d ' ')" == 1 ]] \
    || fail "service must have exactly one desired running task"
  task_id=$(printf '%s\n' "$task_ids" | sed -n '1p')
  task_state=$(docker inspect "$task_id" --format '{{.Status.State}}|{{.DesiredState}}|{{.ServiceID}}')
  [[ "$task_state" == "running|running|$service_id" ]] \
    || fail "task is not running for the exact service ID"
  task_image=$(docker inspect "$task_id" --format '{{.Spec.ContainerSpec.Image}}')
  [[ "$task_image" == "$expected_image" ]] || fail "running task image changed during restart"
  container_id=$(docker inspect "$task_id" --format '{{.Status.ContainerStatus.ContainerID}}')
  [[ "$container_id" =~ ^[0-9a-f]{64}$ ]] || fail "running task has no exact container ID"
  expected_image_id=$(docker image inspect --format '{{.Id}}' "$expected_image")
  [[ "$expected_image_id" =~ ^sha256:[0-9a-f]{64}$ ]] \
    || fail "expected image has no exact local image ID"
  actual_image_id=$(docker inspect "$container_id" --format '{{.Image}}')
  [[ "$actual_image_id" == "$expected_image_id" ]] \
    || fail "running task image ID changed during restart"
}

current_secrets() {
  docker service inspect --format '{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}{{println .SecretName}}{{end}}' "$1"
}

target_secret() {
  local service_name=$1
  docker service inspect \
    --format '{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}{{if eq .File.Name "/app/appsettings.Production.json"}}{{println .SecretName}}{{end}}{{end}}' \
    "$service_name" \
    | sed '/^[[:space:]]*$/d' \
    | head -n1
}

spec_env() {
  docker service inspect --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' "$1"
}

is_forbidden_env_key() {
  case "$1" in
    Security__TokenMint__Key|Jwt__SigningKey|Jwt__PhonePepper|\
    JeebJwt__SigningKey|JeebJwt__PhonePepper|JeebJwt__Issuer|JeebJwt__Audience|UmJwt__SigningKey|\
    PushNotificationServiceApi__InternalApiKey|Whisper__ApiKey|\
    FeatureFlags__Heartbeat__ServiceAuthKey|DATABASE_URL|JEEB_DATABASE_URL|\
    GatewayPostgres__ConnectionString|WalletPostgres__ConnectionString)
      return 0
      ;;
  esac
  return 1
}

assert_safe_spec() {
  local service_name=$1
  local expected_secret=$2
  local actual_secret
  local env_entry

  actual_secret=$(target_secret "$service_name")
  [[ "$actual_secret" == "$expected_secret" ]] \
    || fail "service spec does not use the expected appsettings secret"

  while IFS= read -r env_entry; do
    [[ -n "$env_entry" ]] || continue
    if is_forbidden_env_key "${env_entry%%=*}"; then
      fail "service spec contains a legacy or sensitive environment key"
    fi
  done < <(spec_env "$service_name")
}

wait_for_stable_update() {
  local service_name=$1
  local state
  for ((attempt = 1; attempt <= MAX_WAIT_ATTEMPTS; attempt++)); do
    state=$(docker service inspect --format '{{if .UpdateStatus}}{{.UpdateStatus.State}}{{end}}' "$service_name")
    case "$state" in
      ''|completed) return 0 ;;
      paused) fail "service update paused" ;;
      rollback_*) fail "forbidden automatic rollback state detected" ;;
    esac
    sleep 2
  done
  fail "timed out waiting for service update"
}

secret_is_referenced() {
  local candidate=$1
  local service_id
  local referenced
  while IFS= read -r service_id; do
    [[ -n "$service_id" ]] || continue
    while IFS= read -r referenced; do
      [[ "$referenced" == "$candidate" ]] && return 0
    done < <(current_secrets "$service_id")
  done < <(docker service ls -q)
  return 1
}

remove_inactive_managed_secret() {
  local candidate=$1
  is_managed_secret "$candidate" || fail "refusing to remove an unmanaged secret"
  secret_is_referenced "$candidate" && fail "refusing to remove a referenced secret"
  docker secret rm "$candidate" >/dev/null
}

stabilize() {
  local service_name=$1
  local expected_secret=$2
  local expected_image
  service_exists "$service_name" || fail "cannot stabilize a missing service"
  assert_safe_spec "$service_name" "$expected_secret"
  expected_image=$(current_image "$service_name")
  docker service update --force --detach=false --with-registry-auth \
    --update-order start-first --update-failure-action pause \
    --update-monitor 20s "$service_name" >/dev/null
  wait_for_stable_update "$service_name"
  assert_safe_spec "$service_name" "$expected_secret"
  assert_exact_running_image "$service_name" "$expected_image"
}

finalize() {
  local service_existed=$1
  local service_name=$2
  local new_secret=$3
  local attempted_image=$4

  if [[ "$service_existed" == 0 ]]; then
    if service_exists "$service_name"; then
      [[ "$(current_image "$service_name")" == "$attempted_image" ]] \
        || fail "failed create left a service with an unexpected image"
      fail "new service create failed; leaving the failed service in place for inspection"
    fi
    remove_inactive_managed_secret "$new_secret"
    return
  fi

  # Existing failed updates stay paused with their exact spec and secret references
  # intact for deterministic inspection. Never mutate the service a second time here.
  service_exists "$service_name" || fail "existing service disappeared after failed update"
  fail "existing service update failed; leaving paused state for inspection"
}

garbage_collect() {
  local service_name=$1
  local active_secret
  local candidate
  service_exists "$service_name" || fail "cannot garbage-collect a missing service"
  active_secret=$(target_secret "$service_name")
  is_managed_secret "$active_secret" || fail "current service has no managed appsettings secret"
  while IFS= read -r candidate; do
    is_managed_secret "$candidate" || continue
    if secret_is_referenced "$candidate"; then
      echo "Retaining referenced managed secret"
    else
      docker secret rm "$candidate" >/dev/null
      echo "Removed unreferenced managed secret"
    fi
  done < <(docker secret ls --format '{{.Name}}')
}

command=${1:-}
case "$command" in
  stabilize)
    [[ $# -eq 3 ]] || fail "stabilize requires service and secret names"
    [[ "$2" == jeeb-gateway ]] || fail "invalid service name"
    is_managed_secret "$3" || fail "invalid secret name"
    stabilize "$2" "$3"
    ;;
  verify-safe)
    [[ $# -eq 3 ]] || fail "verify-safe requires service and secret names"
    [[ "$2" == jeeb-gateway ]] || fail "invalid service name"
    is_managed_secret "$3" || fail "invalid secret name"
    assert_safe_spec "$2" "$3"
    ;;
  finalize)
    [[ $# -eq 5 ]] || fail "finalize requires four arguments"
    [[ "$2" == 0 || "$2" == 1 ]] || fail "invalid service existence flag"
    [[ "$3" == jeeb-gateway ]] || fail "invalid service name"
    is_managed_secret "$4" || fail "invalid secret name"
    finalize "$2" "$3" "$4" "$5"
    ;;
  gc)
    [[ $# -eq 2 && "$2" == jeeb-gateway ]] || fail "gc requires jeeb-gateway"
    garbage_collect "$2"
    ;;
  *) fail "unknown lifecycle command" ;;
esac
