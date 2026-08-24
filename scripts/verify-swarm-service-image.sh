#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf 'deployment verification failed: %s\n' "$1" >&2
  exit 1
}

[ "$#" -eq 2 ] || fail "usage: $0 SERVICE EXPECTED_IMAGE"
service=$1
expected_image=$2
[[ "$service" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$ ]] || fail "invalid service name"
[[ "$expected_image" =~ ^[^[:space:]]+@sha256:[0-9a-f]{64}$ ]] \
  || fail "expected image is not an immutable digest reference"

service_id=$(docker service inspect "$service" --format '{{.ID}}')
[[ "$service_id" =~ ^[A-Za-z0-9]+$ ]] || fail "service has no immutable ID"
service_image=$(docker service inspect "$service_id" --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}')
[ "$service_image" = "$expected_image" ] \
  || fail "service spec does not use the exact requested image"

update_state=
for ((attempt = 1; attempt <= 30; attempt++)); do
  update_state=$(docker service inspect "$service_id" --format '{{if .UpdateStatus}}{{.UpdateStatus.State}}{{else}}initial{{end}}')
  case "$update_state" in
    initial|completed|rollback_completed) break ;;
    updating|rollback_started) sleep 4 ;;
    *) fail "service update state is $update_state" ;;
  esac
done
case "$update_state" in
  initial|completed|rollback_completed) ;;
  *) fail "service update did not complete before the verification timeout" ;;
esac

desired_replicas=$(docker service inspect "$service_id" --format '{{.Spec.Mode.Replicated.Replicas}}')
[ "$desired_replicas" = 1 ] || fail "desired replicas is $desired_replicas; expected 1"
task_ids=$(docker service ps "$service_id" --filter desired-state=running --format '{{.ID}}')
[ "$(printf '%s\n' "$task_ids" | sed '/^$/d' | wc -l | tr -d ' ')" = 1 ] \
  || fail "expected exactly one desired running task"
task_id=$(printf '%s\n' "$task_ids" | sed -n '1p')

task_state=$(docker inspect "$task_id" --format '{{.Status.State}}|{{.DesiredState}}|{{.ServiceID}}')
[ "$task_state" = "running|running|$service_id" ] \
  || fail "task is not running for the exact service ID"
task_image=$(docker inspect "$task_id" --format '{{.Spec.ContainerSpec.Image}}')
[ "$task_image" = "$expected_image" ] || fail "task image differs from the requested image"

container_id=$(docker inspect "$task_id" --format '{{.Status.ContainerStatus.ContainerID}}')
[[ "$container_id" =~ ^[0-9a-f]{64}$ ]] || fail "running task has no exact container ID"
expected_image_id=$(docker image inspect "$expected_image" --format '{{.Id}}')
[[ "$expected_image_id" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "requested image has no exact local image ID"
actual_image_id=$(docker inspect "$container_id" --format '{{.Image}}')
[ "$actual_image_id" = "$expected_image_id" ] \
  || fail "container image ID differs from the requested image"

printf 'deployment verification passed: %s runs %s\n' "$service_id" "$service_image"
