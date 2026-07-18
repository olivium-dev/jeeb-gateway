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

is_sensitive_env_key() {
  case "$1" in
    Security__TokenMint__Key|JeebJwt__SigningKey|JeebJwt__PhonePepper|UmJwt__SigningKey|\
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
    if is_sensitive_env_key "${env_entry%%=*}"; then
      fail "$spec_kind spec contains a legacy sensitive environment key"
    fi
  done < <(spec_env "$spec_kind" "$service_name")
}

wait_for_stable_update() {
  local service_name=$1
  local state
  for ((attempt = 1; attempt <= MAX_WAIT_ATTEMPTS; attempt++)); do
    state=$(docker service inspect --format '{{if .UpdateStatus}}{{.UpdateStatus.State}}{{end}}' "$service_name")
    case "$state" in
      ''|completed|rollback_completed) return 0 ;;
      paused|rollback_paused) fail "service update paused" ;;
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
  service_exists "$service_name" || fail "cannot stabilize a missing service"
  assert_safe_spec current "$service_name" "$expected_secret"
  docker service update --force --with-registry-auth \
    --update-order start-first --update-failure-action pause \
    --rollback-order start-first --update-monitor 20s "$service_name" >/dev/null
  wait_for_stable_update "$service_name"
  assert_safe_spec current "$service_name" "$expected_secret"
  assert_safe_spec previous "$service_name" "$expected_secret"
}

recover_existing() {
  local service_name=$1
  local new_secret=$2
  local previous_image=$3
  local old_secret
  local image_uid
  local image_gid
  local -a secret_args=()
  local -a env_args=()
  local env_entry

  service_exists "$service_name" || fail "existing service disappeared during recovery"
  wait_for_stable_update "$service_name"
  old_secret=$(target_secret current "$service_name")
  image_uid=$(docker run --rm --entrypoint /bin/sh "$previous_image" -c 'id -u appuser')
  image_gid=$(docker run --rm --entrypoint /bin/sh "$previous_image" -c 'id -g appuser')
  [[ "$image_uid" =~ ^[0-9]+$ && "$image_gid" =~ ^[0-9]+$ ]] || fail "could not derive app identity"

  if [[ -n "$old_secret" && "$old_secret" != "$new_secret" ]]; then
    secret_args+=(--secret-rm "$old_secret")
  fi
  if [[ "$old_secret" != "$new_secret" ]]; then
    secret_args+=(--secret-add "source=$new_secret,target=$SECRET_TARGET,uid=$image_uid,gid=$image_gid,mode=0400")
  fi
  while IFS= read -r env_entry; do
    [[ -n "$env_entry" ]] || continue
    if is_sensitive_env_key "${env_entry%%=*}"; then
      env_args+=(--env-rm "${env_entry%%=*}")
    fi
  done < <(spec_env current "$service_name")

  # This is deliberately an explicit update to the previously captured digest,
  # not a best-effort `service rollback` to an unverified PreviousSpec.
  docker service update --image "$previous_image" --with-registry-auth \
    "${secret_args[@]}" "${env_args[@]}" \
    --update-order start-first --update-failure-action pause \
    --rollback-order start-first --update-monitor 20s "$service_name" >/dev/null
  wait_for_stable_update "$service_name"
  [[ "$(current_image "$service_name")" == "$previous_image" ]] \
    || fail "explicit rollback did not restore the captured digest"
  assert_safe_spec current "$service_name" "$new_secret"
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

  [[ "$previous_image" != none ]] || fail "existing service has no rollback digest"
  recover_existing "$service_name" "$new_secret" "$previous_image"
  stabilize "$service_name" "$new_secret"
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
