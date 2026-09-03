#!/usr/bin/env bash
set -euo pipefail

gateway_service=${1:?gateway service is required}
push_service=${2:?push service is required}
expected_key_sha=${3:?protected relay-key digest is required}

case "$gateway_service:$push_service" in
  jeeb-staging-jeeb-gateway:jeeb-staging-push-notification) ;;
  jeeb-production-jeeb-gateway:push-notification) ;;
  *)
    echo 'FAIL: unapproved Jeeb Chat B service pair' >&2
    exit 64
    ;;
esac
[[ "$expected_key_sha" =~ ^[0-9a-f]{64}$ ]] || {
  echo 'FAIL: relay-key proof is not an exact SHA-256 digest' >&2
  exit 65
}

push_modes=()
while IFS= read -r row; do push_modes+=("$row"); done < <(
  docker service inspect "$push_service" \
    --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' \
    | awk -F= 'tolower($1)=="push_auth_mode" {print $0}'
)
[ "${#push_modes[@]}" -eq 1 ] && [ "${push_modes[0]}" = PUSH_AUTH_MODE=expand ] || {
  echo 'FAIL: push-notification is not exact provider expand' >&2
  exit 66
}

chat_flags=()
while IFS= read -r row; do chat_flags+=("$row"); done < <(
  docker service inspect "$gateway_service" \
    --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' \
    | awk -F= 'tolower($1)=="featureflags__useupstream__chat" {print $0}'
)
[ "${#chat_flags[@]}" -eq 1 ] \
  && [ "${chat_flags[0]}" = FeatureFlags__UseUpstream__Chat=false ] || {
  echo 'FAIL: gateway is not in exact A1 Chat=false posture' >&2
  exit 67
}

key_paths=()
while IFS= read -r row; do key_paths+=("$row"); done < <(
  docker service inspect "$gateway_service" \
    --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' \
    | awk -F= 'tolower($1)=="pushnotificationserviceapi__gatewayapikeyfile" {print $0}'
)
[ "${#key_paths[@]}" -eq 1 ] \
  && [ "${key_paths[0]}" = PushNotificationServiceApi__GatewayApiKeyFile=/run/secrets/push_gateway_api_key ] || {
  echo 'FAIL: gateway relay-key path is not exact' >&2
  exit 68
}

gateway_base_urls=()
while IFS= read -r row; do gateway_base_urls+=("$row"); done < <(
  docker service inspect "$gateway_service" \
    --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' \
    | awk -F= 'tolower($1)=="pushnotificationserviceapi__baseurl" {print $0}'
)
expected_provider_url="http://$push_service:8080"
[ "${#gateway_base_urls[@]}" -eq 1 ] \
  && [ "${gateway_base_urls[0]}" = "PushNotificationServiceApi__BaseUrl=$expected_provider_url" ] || {
  echo 'FAIL: gateway push provider URL is not the exact approved Swarm DNS target' >&2
  exit 68
}

key_mounts=()
while IFS= read -r row; do key_mounts+=("$row"); done < <(
  docker service inspect "$gateway_service" --format \
    '{{range .Spec.TaskTemplate.ContainerSpec.Secrets}}{{if eq .File.Name "push_gateway_api_key"}}{{printf "%s|%s|%s|%s|%d\n" .SecretName .File.Name .File.UID .File.GID .File.Mode}}{{end}}{{end}}'
)
[ "${#key_mounts[@]}" -eq 1 ] || {
  echo 'FAIL: gateway must mount exactly one relay key at the sanctioned target' >&2
  exit 69
}
IFS='|' read -r key_source key_target key_uid key_gid key_mode <<<"${key_mounts[0]}"
[[ "$key_source" =~ ^jeeb_(staging_)?gateway_push_token_[0-9]+_[0-9]+$ ]] \
  && [ "$key_target" = push_gateway_api_key ] \
  && [ "$key_uid" = 65532 ] \
  && [ "$key_gid" = 65532 ] \
  && [ "$key_mode" = 256 ] || {
  echo 'FAIL: gateway relay-key mount source, ownership, or mode is not exact' >&2
  exit 70
}

for service in "$gateway_service" "$push_service"; do
  image=$(docker service inspect "$service" --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}')
  [[ "$image" =~ @sha256:[0-9a-f]{64}$ ]] || {
    echo 'FAIL: B activation requires digest-pinned gateway and provider images' >&2
    exit 71
  }
done

running_tasks=()
while IFS= read -r row; do running_tasks+=("$row"); done < <(
  docker service ps "$gateway_service" --filter desired-state=running --format '{{.ID}}'
)
[ "${#running_tasks[@]}" -eq 1 ] && [[ "${running_tasks[0]}" =~ ^[a-zA-Z0-9]+$ ]] || {
  echo 'FAIL: gateway must have exactly one running task' >&2
  exit 72
}
container_id=$(docker inspect "${running_tasks[0]}" --format '{{.Status.ContainerStatus.ContainerID}}')
[[ "$container_id" =~ ^[a-f0-9]{12,64}$ ]] || {
  echo 'FAIL: gateway running container could not be resolved' >&2
  exit 73
}
[ "$(docker inspect "$container_id" --format '{{.State.Running}}')" = true ] || {
  echo 'FAIL: gateway task container is not running' >&2
  exit 74
}
mounted_key_sha=$(docker exec "$container_id" sha256sum /run/secrets/push_gateway_api_key | awk '{print $1}')
[ "$mounted_key_sha" = "$expected_key_sha" ] || {
  echo 'FAIL: mounted relay key does not match the protected activation input' >&2
  exit 75
}

readiness=$(docker exec "$container_id" sh -eu -c '
  relay_key=$(tr -d "\r\n" </run/secrets/push_gateway_api_key)
  [ -n "$relay_key" ]
  exec wget -qO- --timeout=10 \
    --header="X-Caller-Id: jeeb-gateway" \
    --header="X-Api-Key: $relay_key" \
    "$1/api/v1/register/ready"
' sh "$expected_provider_url")
printf '%s' "$readiness" | jq -e \
  'type == "object" and .status == "ready" and .scope == "gateway.registration"' \
  >/dev/null || {
  echo 'FAIL: authenticated provider relay-expand scope proof failed' >&2
  exit 76
}

echo 'Jeeb Chat B provider expand, A1, immutable-image, mount, and scoped-key preflight: PASS'
