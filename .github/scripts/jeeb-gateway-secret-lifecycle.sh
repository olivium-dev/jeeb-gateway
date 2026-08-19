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
  local actual_image
  local expected_image_id
  local task_image
  local task_image_id
  local -a running_containers=()

  [[ "$expected_image" == *@sha256:* ]] || fail "expected service image is not digest-pinned"
  actual_image=$(current_image "$service_name")
  [[ "$actual_image" == "$expected_image" ]] || fail "service image changed during restart"
  expected_image_id=$(docker image inspect --format '{{.Id}}' "$expected_image")
  mapfile -t running_containers < <(
    docker ps -q --filter "label=com.docker.swarm.service.name=$service_name"
  )
  [[ ${#running_containers[@]} -eq 1 ]] || fail "expected exactly one running service container"
  task_image=$(docker inspect --format '{{.Config.Image}}' "${running_containers[0]}")
  task_image_id=$(docker inspect --format '{{.Image}}' "${running_containers[0]}")
  [[ "$task_image" == "$expected_image" ]] || fail "running task image changed during restart"
  [[ "$task_image_id" == "$expected_image_id" ]] || fail "running task image ID changed during restart"
}

current_secrets() {
  docker service inspect --format '{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}{{println .SecretName}}{{end}}' "$1"
}

previous_secrets() {
  docker service inspect --format '{{with .PreviousSpec}}{{range .TaskTemplate.ContainerSpec.Secrets}}{{println .SecretName}}{{end}}{{end}}' "$1"
}

target_secret() {
  local spec_kind=$1
  local service_name=$2
  local format
  case "$spec_kind" in
    current)
      format='{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}{{if eq .File.Name "/app/appsettings.Production.json"}}{{println .SecretName}}{{end}}{{end}}'
      ;;
    previous)
      format='{{with .PreviousSpec}}{{range .TaskTemplate.ContainerSpec.Secrets}}{{if eq .File.Name "/app/appsettings.Production.json"}}{{println .SecretName}}{{end}}{{end}}{{end}}'
      ;;
    *) fail "unknown spec kind" ;;
  esac
  docker service inspect --format "$format" "$service_name" \
    | sed '/^[[:space:]]*$/d' \
    | head -n1
}

spec_env() {
  local spec_kind=$1
  local service_name=$2
  case "$spec_kind" in
    current)
      docker service inspect --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' "$service_name"
      ;;
    previous)
      docker service inspect --format '{{with .PreviousSpec}}{{range .TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}{{end}}' "$service_name"
      ;;
    *) fail "unknown spec kind" ;;
  esac
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
  local spec_kind=$1
  local service_name=$2
  local expected_secret=$3
  local actual_secret
  local env_entry

  actual_secret=$(target_secret "$spec_kind" "$service_name")
  [[ "$actual_secret" == "$expected_secret" ]] \
    || fail "$spec_kind spec does not use the expected appsettings secret"

  while IFS= read -r env_entry; do
    [[ -n "$env_entry" ]] || continue
    if is_forbidden_env_key "${env_entry%%=*}"; then
      fail "$spec_kind spec contains a legacy or sensitive environment key"
    fi
  done < <(spec_env "$spec_kind" "$service_name")
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

wait_for_service_absent() {
  local service_name=$1
  for ((attempt = 1; attempt <= MAX_WAIT_ATTEMPTS; attempt++)); do
    service_exists "$service_name" || return 0
    sleep 1
  done
  fail "timed out waiting for failed create removal"
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
    while IFS= read -r referenced; do
      [[ "$referenced" == "$candidate" ]] && return 0
    done < <(previous_secrets "$service_id")
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
  assert_safe_spec current "$service_name" "$expected_secret"
  expected_image=$(current_image "$service_name")
  docker service update --force --detach=false --with-registry-auth \
    --update-order start-first --update-failure-action pause \
    --update-monitor 20s "$service_name" >/dev/null
  wait_for_stable_update "$service_name"
  assert_safe_spec current "$service_name" "$expected_secret"
  assert_safe_spec previous "$service_name" "$expected_secret"
  assert_exact_running_image "$service_name" "$expected_image"
}

finalize() {
  local service_existed=$1
  local service_name=$2
  local new_secret=$3
  local previous_image=$4
  local attempted_image=$5

  if [[ "$service_existed" == 0 ]]; then
    if service_exists "$service_name"; then
      [[ "$(current_image "$service_name")" == "$attempted_image" ]] \
        || fail "refusing to remove an unrelated service"
      docker service rm "$service_name" >/dev/null
      wait_for_service_absent "$service_name"
    fi
    remove_inactive_managed_secret "$new_secret"
    return
  fi

  # Existing failed updates stay paused with their exact spec and secret references
  # intact for deterministic inspection. Never mutate the service a second time here.
  service_exists "$service_name" || fail "existing service disappeared after failed update"
  [[ "$previous_image" != none ]] || fail "existing service has no captured prior image"
  fail "existing service update failed; leaving paused state for inspection"
}

garbage_collect() {
  local service_name=$1
  local active_secret
  local candidate
  service_exists "$service_name" || fail "cannot garbage-collect a missing service"
  active_secret=$(target_secret current "$service_name")
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
    assert_safe_spec current "$2" "$3"
    assert_safe_spec previous "$2" "$3"
    ;;
  finalize)
    [[ $# -eq 6 ]] || fail "finalize requires five arguments"
    [[ "$2" == 0 || "$2" == 1 ]] || fail "invalid service existence flag"
    [[ "$3" == jeeb-gateway ]] || fail "invalid service name"
    is_managed_secret "$4" || fail "invalid secret name"
    finalize "$2" "$3" "$4" "$5" "$6"
    ;;
  gc)
    [[ $# -eq 2 && "$2" == jeeb-gateway ]] || fail "gc requires jeeb-gateway"
    garbage_collect "$2"
    ;;
  *) fail "unknown lifecycle command" ;;
esac
