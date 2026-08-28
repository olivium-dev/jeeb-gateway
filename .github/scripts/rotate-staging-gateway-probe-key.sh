#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "RED: $1" >&2
  exit 1
}

service=jeeb-staging-jeeb-gateway
probe_target=staging_wss_probe_mint_key
expected_image=${2:-}
new_probe_secret=${3:-}
docker_config_relative=${4:-}

[[ "$expected_image" =~ ^ghcr\.io/olivium-dev/jeeb-gateway@sha256:[0-9a-f]{64}$ ]] \
  || fail "expected image is not the approved immutable gateway digest"
[[ "$new_probe_secret" =~ ^jeeb_staging_gateway_wss_probe_[0-9]+_[0-9]+$ ]] \
  || fail "new probe secret name is outside the run-scoped contract"
[[ "$docker_config_relative" =~ ^\.jeeb-deploy/ghcr-[0-9]+-[0-9]+$ ]] \
  || fail "remote Docker credential path is outside the run-scoped contract"
export DOCKER_CONFIG="$HOME/$docker_config_relative"

service_version() {
  docker service inspect "$service" --format '{{.Version.Index}}'
}

service_replicas() {
  docker service inspect "$service" --format '{{.Spec.Mode.Replicated.Replicas}}'
}

probe_source() {
  docker service inspect "$service" \
    --format '{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}{{if eq .File.Name "staging_wss_probe_mint_key"}}{{println .SecretName}}{{end}}{{end}}'
}

wait_for_update() {
  local state
  for _ in $(seq 1 120); do
    state=$(docker service inspect "$service" \
      --format '{{if .UpdateStatus}}{{.UpdateStatus.State}}{{end}}')
    case "$state" in
      ''|completed|rollback_completed) return 0 ;;
      paused|rollback_paused) fail "gateway update entered a paused state" ;;
    esac
    sleep 2
  done
  fail "gateway update did not reach a terminal state"
}

task_is_healthy() {
  local task_id=$1 container_id health
  container_id=$(docker inspect "$task_id" \
    --format '{{if .Status.ContainerStatus}}{{.Status.ContainerStatus.ContainerID}}{{end}}')
  [[ "$container_id" =~ ^[0-9a-f]{64}$ ]] || return 1
  health=$(docker inspect "$container_id" \
    --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}')
  [ "$health" = healthy ]
}

wait_for_healthy_replicas() {
  local expected=$1 task_ids task_id healthy
  for _ in $(seq 1 80); do
    mapfile -t task_ids < <(
      docker service ps "$service" --filter desired-state=running -q | sed '/^$/d'
    )
    healthy=0
    if [ "${#task_ids[@]}" -eq "$expected" ]; then
      for task_id in "${task_ids[@]}"; do
        if task_is_healthy "$task_id"; then
          healthy=$((healthy + 1))
        fi
      done
      [ "$healthy" -eq "$expected" ] && return 0
    fi
    sleep 2
  done
  fail "gateway replicas did not become healthy"
}

assert_exact_env() {
  local environment key value matches
  environment=$1
  key=$2
  value=$3
  matches=$(printf '%s\n' "$environment" | awk -F= \
    -v expected="$key" -v value="$value" '
      tolower($1) == tolower(expected) {
        count++
        if (substr($0, index($0, "=") + 1) == value) exact++
      }
      END { print count + 0 ":" exact + 0 }
    ')
  [ "$matches" = 1:1 ] || fail "Gateway B environment contract drifted at $key"
}

assert_gateway_b() {
  local expected_replicas=$1 image ports environment update_order update_failure
  local network_ids network_id network_contract source_count
  image=$(docker service inspect "$service" \
    --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}')
  [ "$image" = "$expected_image" ] || fail "gateway image digest drifted"
  [ "$(service_replicas)" = "$expected_replicas" ] \
    || fail "gateway replica count drifted"
  ports=$(docker service inspect "$service" \
    --format '{{range .Endpoint.Spec.Ports}}{{.PublishedPort}}:{{.TargetPort}}:{{.PublishMode}}{{end}}')
  [ "$ports" = 10000:8080:ingress ] || fail "gateway ingress topology drifted"
  update_order=$(docker service inspect "$service" --format '{{.Spec.UpdateConfig.Order}}')
  update_failure=$(docker service inspect "$service" --format '{{.Spec.UpdateConfig.FailureAction}}')
  [ "$update_order" = start-first ] || fail "gateway update order is not start-first"
  [ "$update_failure" = rollback ] || fail "gateway update failure action is not rollback"

  environment=$(docker service inspect "$service" \
    --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}')
  assert_exact_env "$environment" FeatureFlags__UseUpstream__Otp true
  assert_exact_env "$environment" FeatureFlags__UseUpstream__Chat true
  assert_exact_env "$environment" FeatureFlags__UseUpstream__Realtime true
  assert_exact_env "$environment" Features__RealtimeWebSocketProxy__Enabled true
  assert_exact_env "$environment" FeatureFlags__UseUpstream__Voice false
  assert_exact_env "$environment" Services__ServiceOTP__BaseUrl \
    http://jeeb-staging-one-time-password:8080
  assert_exact_env "$environment" ServiceOTPApi__BaseUrl \
    http://jeeb-staging-one-time-password:8080
  assert_exact_env "$environment" Auth__Otp__ApplicationId \
    0d51afe1-499f-4a29-a55a-36d2dd223b05
  assert_exact_env "$environment" Auth__Otp__Phone__AllowedRegion LB
  assert_exact_env "$environment" Auth__Otp__Phone__EnforceRegion false
  assert_exact_env "$environment" Services__Realtime__BaseUrl \
    http://jeeb-staging-realtime-comunication-service:4000
  assert_exact_env "$environment" Services__Realtime__PublicSocketUrl \
    wss://app.jeeb.fds-1.com/socket/websocket
  assert_exact_env "$environment" Operations__RealtimeProbe__MintKeyFile \
    /run/secrets/staging_wss_probe_mint_key
  assert_exact_env "$environment" SuperLogin__OpenMode true
  assert_exact_env "$environment" DemoUsers__Enabled true
  assert_exact_env "$environment" Features__DevEndpoints__Enabled true
  assert_exact_env "$environment" Features__Swagger__Enabled true
  banned_host="192.168.2.""50"
  ! printf '%s\n' "$environment" | grep -Fq "$banned_host" \
    || fail "gateway environment contains the retired host"
  ! printf '%s\n' "$environment" | grep -Eiq \
    '(^|[^a-z0-9])((unified[-_. ]*payment([-_. ]*gateway)?)|(payment[-_. ]*gateway)|upg)([^a-z0-9]|$)' \
    || fail "gateway environment contains a forbidden payment-gateway route"

  mapfile -t network_ids < <(
    docker service inspect "$service" \
      --format '{{range .Spec.TaskTemplate.Networks}}{{println .Target}}{{end}}' \
      | sed '/^$/d'
  )
  [ "${#network_ids[@]}" -eq 1 ] || fail "gateway network attachment count drifted"
  network_id=${network_ids[0]}
  network_contract=$(docker network inspect "$network_id" \
    --format '{{.Driver}}|{{.Scope}}|{{json .Options}}')
  [[ "$network_contract" == overlay\|swarm\|*'"encrypted"'* ]] \
    || fail "gateway overlay is not encrypted"

  source_count=$(probe_source | sed '/^$/d' | wc -l | tr -d ' ')
  [ "$source_count" -eq 1 ] || fail "probe-secret target is not unique"
}

update_service() {
  docker service update --detach="$1" --image "$expected_image" \
    --with-registry-auth --update-order start-first \
    --update-failure-action rollback --update-monitor 30s \
    --update-parallelism 1 --update-delay "$2" "${@:3}" "$service" >/dev/null
}

secret_is_referenced() {
  local candidate=$1 service_id
  while IFS= read -r service_id; do
    [ -n "$service_id" ] || continue
    if docker service inspect "$service_id" \
      --format '{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}{{println .SecretName}}{{end}}' \
      | grep -Fxq "$candidate"; then
      return 0
    fi
  done < <(docker service ls -q)
  return 1
}

mode=${1:-}
case "$mode" in
  rotate)
    assert_gateway_b 1
    old_probe_secret=$(probe_source)
    [ "$old_probe_secret" != "$new_probe_secret" ] \
      || fail "new probe secret is already mounted"
    docker secret inspect "$new_probe_secret" >/dev/null \
      || fail "new probe secret does not exist"

    update_service false 0s --replicas 2
    wait_for_update
    assert_gateway_b 2
    wait_for_healthy_replicas 2
    mapfile -t incumbent_tasks < <(
      docker service ps "$service" --filter desired-state=running -q | sed '/^$/d'
    )
    [ "${#incumbent_tasks[@]}" -eq 2 ] || fail "incumbent scale-up was not exact"

    update_service true 30s \
      --secret-rm "$old_probe_secret" \
      --secret-add "source=$new_probe_secret,target=$probe_target,uid=65532,gid=65532,mode=0400"

    witnessed=false
    for _ in $(seq 1 160); do
      mapfile -t running_tasks < <(
        docker service ps "$service" --filter desired-state=running -q | sed '/^$/d'
      )
      old_running=0
      for task_id in "${running_tasks[@]}"; do
        for incumbent in "${incumbent_tasks[@]}"; do
          [ "$task_id" = "$incumbent" ] && old_running=$((old_running + 1))
        done
      done
      for task_id in "${running_tasks[@]}"; do
        is_incumbent=false
        for incumbent in "${incumbent_tasks[@]}"; do
          [ "$task_id" = "$incumbent" ] && is_incumbent=true
        done
        if [ "$is_incumbent" = false ] && [ "$old_running" -ge 1 ] \
          && task_is_healthy "$task_id"; then
          candidate_source=$(docker inspect "$task_id" \
            --format '{{range .Spec.ContainerSpec.Secrets}}{{if eq .File.Name "staging_wss_probe_mint_key"}}{{println .SecretName}}{{end}}{{end}}')
          if [ "$candidate_source" = "$new_probe_secret" ]; then
            witnessed=true
            break 2
          fi
        fi
      done
      sleep 0.5
    done
    [ "$witnessed" = true ] \
      || fail "no healthy rotated candidate was witnessed beside an incumbent"

    wait_for_update
    [ "$(docker service inspect "$service" \
      --format '{{.UpdateStatus.State}}')" = completed ] \
      || fail "probe-key rotation did not complete forward"
    assert_gateway_b 2
    [ "$(probe_source)" = "$new_probe_secret" ] \
      || fail "rotated probe secret is not authoritative"
    wait_for_healthy_replicas 2
    printf 'OLD_PROBE_SECRET=%s\n' "$old_probe_secret"
    printf 'ROTATED_VERSION=%s\n' "$(service_version)"
    printf 'CANDIDATE_HEALTHY_WITH_INCUMBENT=1\n'
    ;;

  finalize)
    old_probe_secret=${5:-}
    [[ "$old_probe_secret" =~ ^jeeb_staging_gateway_wss_probe_[A-Za-z0-9_-]+$ ]] \
      || fail "old probe secret name is outside the managed contract"
    assert_gateway_b 2
    [ "$(probe_source)" = "$new_probe_secret" ] \
      || fail "new probe secret was not mounted before finalization"
    wait_for_healthy_replicas 2
    update_service false 0s --replicas 1
    wait_for_update
    assert_gateway_b 1
    [ "$(probe_source)" = "$new_probe_secret" ] \
      || fail "new probe secret drifted during finalization"
    wait_for_healthy_replicas 1
    if ! secret_is_referenced "$old_probe_secret"; then
      docker secret rm "$old_probe_secret" >/dev/null
    fi
    printf 'FINAL_VERSION=%s\n' "$(service_version)"
    ;;

  rollback)
    old_probe_secret=${5:-}
    [[ "$old_probe_secret" =~ ^jeeb_staging_gateway_wss_probe_[A-Za-z0-9_-]+$ ]] \
      || fail "old probe secret name is outside the managed contract"
    current_probe_secret=$(probe_source)
    if [ "$current_probe_secret" = "$new_probe_secret" ]; then
      docker service rollback --detach=false "$service" >/dev/null || true
      wait_for_update
    elif [ "$current_probe_secret" != "$old_probe_secret" ]; then
      fail "rollback found an unknown authoritative probe secret"
    fi
    if [ "$(service_replicas)" != 1 ]; then
      update_service false 0s --replicas 1
      wait_for_update
    fi
    assert_gateway_b 1
    [ "$(probe_source)" = "$old_probe_secret" ] \
      || fail "rollback did not restore the incumbent probe secret"
    wait_for_healthy_replicas 1
    if ! secret_is_referenced "$new_probe_secret"; then
      docker secret rm "$new_probe_secret" >/dev/null
    fi
    ;;

  *) fail "mode must be rotate, finalize, or rollback" ;;
esac
