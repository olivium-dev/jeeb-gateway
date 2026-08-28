#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cutover="$repository_root/scripts/staging-gateway-security-cutover.sh"
test_root=$(mktemp -d)
trap 'status=$?; rm -rf -- "$test_root"; exit "$status"' EXIT
# shellcheck source=staging-gateway-security-cutover.sh disable=SC1091
source "$cutover"

incumbent_image=ghcr.io/olivium-dev/jeeb-gateway@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
candidate_image=ghcr.io/olivium-dev/jeeb-gateway@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
service_id=serviceabc
printf '%s\n' '{"Name":"incumbent"}' > "$test_root/incumbent.json"
printf '%s\n' '{"Name":"candidate"}' > "$test_root/candidate.json"
printf '%s\n' 10 > "$test_root/incumbent-version"
printf '%s\n' "$service_id" > "$test_root/incumbent-id"

forward_state_file="$test_root/forward-state"
forward_status_file="$test_root/forward-status"
forward_submit_count="$test_root/forward-submit-count"
forward_capture_count="$test_root/forward-capture-count"

staging_gateway_lock_assert() { return 0; }
capture_remote_spec() {
  local spec=$1 version=$2 id=$3 state count
  count=$(cat "$forward_capture_count")
  printf '%s\n' "$((count + 1))" > "$forward_capture_count"
  state=$(cat "$forward_state_file")
  if [ "$state" = candidate ]; then
    jq '.' "$test_root/candidate.json" > "$spec"
  else
    cp "$test_root/incumbent.json" "$spec"
  fi
  if [ "$state" = incumbent ]; then
    cp "$test_root/incumbent-version" "$version"
  else
    printf '%s\n' 11 > "$version"
  fi
  cp "$test_root/incumbent-id" "$id"
}
staging_gateway_submit_spec_cas() {
  local count status
  count=$(cat "$forward_submit_count")
  printf '%s\n' "$((count + 1))" > "$forward_submit_count"
  status=$(cat "$forward_status_file")
  if [ "${FORWARD_MUTATE:-false}" = true ]; then
    printf '%s\n' candidate > "$forward_state_file"
  fi
  [ "$status" != error ] || return 1
  printf '%s' "$status"
}

reset_forward() {
  printf '%s\n' incumbent > "$forward_state_file"
  printf '%s\n' 200 > "$forward_status_file"
  printf '%s\n' 0 > "$forward_submit_count"
  printf '%s\n' 0 > "$forward_capture_count"
  : > "$test_root/candidate-version"
  : > "$test_root/candidate-id"
  : > "$test_root/result"
}

run_forward() {
  staging_gateway_security_cutover_forward_apply \
    "$test_root/incumbent.json" "$test_root/incumbent-version" \
    "$test_root/incumbent-id" "$test_root/candidate.json" \
    "$test_root/candidate-version" "$test_root/candidate-id" \
    "$test_root" "$test_root/result"
}

reset_forward
FORWARD_MUTATE=true run_forward
[ "$(cat "$test_root/result")" = security-cutover-http-200-exact-candidate ]
[ "$(cat "$test_root/candidate-version")" = 11 ]
[ "$(cat "$forward_submit_count")" -eq 1 ]
if cmp -s "$test_root/security-cutover-observed-spec.json" "$test_root/candidate.json"; then
  echo 'semantic security-cutover fixture unexpectedly remained byte-identical' >&2
  exit 1
fi
staging_gateway_specs_equal \
  "$test_root/security-cutover-observed-spec.json" "$test_root/candidate.json"

for rejected_status in 409 500 error; do
  reset_forward
  printf '%s\n' "$rejected_status" > "$forward_status_file"
  set +e
  FORWARD_MUTATE=false run_forward >/dev/null 2>&1
  status=$?
  set -e
  [ "$status" -ne 0 ]
  [ "$(cat "$forward_submit_count")" -eq 1 ]
  [ "$(cat "$forward_capture_count")" -eq 1 ]
done

# A lost response after an accepted CAS remains frozen/fix-forward. It is not
# retried or reconciled by a second mutation.
reset_forward
printf '%s\n' error > "$forward_status_file"
set +e
FORWARD_MUTATE=true run_forward >/dev/null 2>&1
lost_status=$?
set -e
[ "$lost_status" -ne 0 ]
[ "$(cat "$test_root/result")" = security-cutover-ambiguous-fix-forward ]
[ "$(cat "$forward_submit_count")" -eq 1 ]
[ "$(cat "$forward_capture_count")" -eq 1 ]
[ "$(cat "$forward_state_file")" = candidate ]

fake_bin="$test_root/bin"
mkdir -p "$fake_bin"
cat > "$fake_bin/docker" <<'FAKE_DOCKER'
#!/usr/bin/env bash
set -euo pipefail

service_id=${CUTOVER_TEST_SERVICE_ID:?}
incumbent_image=${CUTOVER_TEST_INCUMBENT_IMAGE:?}
candidate_image=${CUTOVER_TEST_CANDIDATE_IMAGE:?}
phase=${CUTOVER_TEST_PHASE:?}
scenario=${CUTOVER_TEST_SCENARIO:-success}
old_container=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
new_container=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
new_container_2=cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc

if [ "$1 $2" = 'service inspect' ]; then
  if [ "$scenario" = service-inspect-error ]; then exit 1; fi
  if [ "$phase" = capture ]; then
    image=$incumbent_image
    version=10
    update_state=''
    order=start-first
    failure=rollback
  else
    image=$candidate_image
    version=11
    update_state=completed
    order=stop-first
    failure=pause
    [ "$scenario" != paused ] || update_state=paused
    [ "$scenario" != updating ] || update_state=updating
  fi
  jq -cn --arg id "$service_id" --arg image "$image" \
    --arg state "$update_state" --arg order "$order" --arg failure "$failure" \
    --argjson version "$version" '
      {
        ID:$id,
        Version:{Index:$version},
        Spec:{
          TaskTemplate:{ContainerSpec:{Image:$image}},
          Mode:{Replicated:{Replicas:1}},
          UpdateConfig:{Parallelism:1,Monitor:20000000000,Order:$order,FailureAction:$failure}
        },
        UpdateStatus:{State:$state}
      }
    '
  exit 0
fi

if [ "$1 $2" = 'service ps' ]; then
  if [ "$phase" = capture ]; then
    printf '%s\n' oldtask
    [ "$scenario" != multiple-incumbents ] || printf '%s\n' oldtask2
  else
    if [ "$scenario" = extra-candidate ]; then
      printf '%s\n' newtask newtask2
    elif [ "$scenario" = unstable ]; then
      count=$(cat "$CUTOVER_TEST_COUNTER")
      printf '%s\n' "$((count + 1))" > "$CUTOVER_TEST_COUNTER"
      if [ $((count % 2)) -eq 0 ]; then printf '%s\n' newtask; else printf '%s\n' newtask2; fi
    else
      printf '%s\n' newtask
    fi
  fi
  exit 0
fi

if [ "$1" = inspect ]; then
  object=$2
  [ "$scenario" != task-inspect-error ] || exit 1
  case "$object" in
    oldtask|oldtask2)
      state=running; desired=running
      if [ "$phase" = verify ]; then state=shutdown; desired=shutdown; fi
      [ "$scenario" != old-running ] || { state=running; desired=shutdown; }
      jq -cn --arg service_id "$service_id" --arg image "$incumbent_image" \
        --arg state "$state" --arg desired "$desired" --arg container "$old_container" '
          {ServiceID:$service_id,DesiredState:$desired,Status:{State:$state,ContainerStatus:{ContainerID:$container}},Spec:{ContainerSpec:{Image:$image}}}
        '
      ;;
    newtask|newtask2)
      container=$new_container
      [ "$object" != newtask2 ] || container=$new_container_2
      task_state=running
      [ "$scenario" != candidate-failed ] || task_state=failed
      jq -cn --arg service_id "$service_id" --arg image "$candidate_image" \
        --arg container "$container" --arg state "$task_state" '
          {ServiceID:$service_id,DesiredState:"running",Status:{State:$state,ContainerStatus:{ContainerID:$container}},Spec:{ContainerSpec:{Image:$image}}}
        '
      ;;
    *) exit 1 ;;
  esac
  exit 0
fi

if [ "$1 $2" = 'container inspect' ]; then
  container=$3
  if [ "$phase" = verify ] && [ "$container" = "$old_container" ]; then
    [ "$scenario" = old-container-running ] || exit 1
    jq -cn '{State:{Running:true,Health:{Status:"healthy"}}}'
    exit 0
  fi
  health=healthy
  [ "$scenario" != unhealthy ] || health=unhealthy
  jq -cn --arg health "$health" '{State:{Running:true,Health:{Status:$health}}}'
  exit 0
fi

if [ "$1 $2" = 'container ls' ]; then
  exit 0
fi

exit 97
FAKE_DOCKER
chmod +x "$fake_bin/docker"

export CUTOVER_TEST_SERVICE_ID=$service_id
export CUTOVER_TEST_INCUMBENT_IMAGE=$incumbent_image
export CUTOVER_TEST_CANDIDATE_IMAGE=$candidate_image
export CUTOVER_TEST_COUNTER="$test_root/counter"
printf '%s\n' 0 > "$CUTOVER_TEST_COUNTER"

export CUTOVER_TEST_PHASE=capture CUTOVER_TEST_SCENARIO=success
PATH="$fake_bin:$PATH" bash "$cutover" --execute capture \
  jeeb-staging-jeeb-gateway "$service_id" "$incumbent_image" \
  > "$test_root/incumbent-tasks.json"
jq -e '.ServiceID == "serviceabc" and (.Tasks | length == 1)' \
  "$test_root/incumbent-tasks.json" >/dev/null

set +e
CUTOVER_TEST_SCENARIO=multiple-incumbents PATH="$fake_bin:$PATH" \
  bash "$cutover" --execute capture jeeb-staging-jeeb-gateway \
    "$service_id" "$incumbent_image" >/dev/null 2>&1
multiple_status=$?
set -e
[ "$multiple_status" -ne 0 ]

manifest_base64=$(base64 < "$test_root/incumbent-tasks.json" | tr -d '\n')
candidate_service="$test_root/candidate-service.json"
CUTOVER_TEST_PHASE=verify CUTOVER_TEST_SCENARIO=success PATH="$fake_bin:$PATH" \
  docker service inspect jeeb-staging-jeeb-gateway --format '{{json .}}' \
  > "$candidate_service"
candidate_spec_sha=$(jq -e -S -c '.Spec | if type == "object" then . else error("Spec") end' \
  "$candidate_service" | sha256sum | awk '{print $1}')

run_verify_case() {
  local scenario=$1 expected=$2 supplied_sha=${3:-$candidate_spec_sha}
  printf '%s\n' 0 > "$CUTOVER_TEST_COUNTER"
  set +e
  output=$(CUTOVER_TEST_PHASE=verify CUTOVER_TEST_SCENARIO="$scenario" \
    STAGING_GATEWAY_CUTOVER_MAX_ATTEMPTS=1 \
    STAGING_GATEWAY_CUTOVER_POLL_SECONDS=0 \
    PATH="$fake_bin:$PATH" bash "$cutover" --execute verify \
      jeeb-staging-jeeb-gateway "$service_id" "$candidate_image" \
      "$supplied_sha" 11 "$manifest_base64" 2>&1)
  status=$?
  set -e
  if [ "$expected" = pass ]; then
    [ "$status" -eq 0 ] || {
      printf 'expected runtime proof success for %s: %s\n' "$scenario" "$output" >&2
      exit 1
    }
  else
    [ "$status" -ne 0 ] || {
      printf 'unsafe runtime proof passed: %s\n' "$scenario" >&2
      exit 1
    }
  fi
}

run_verify_case success pass
run_verify_case paused reject
run_verify_case updating reject
run_verify_case old-running reject
run_verify_case old-container-running reject
run_verify_case extra-candidate reject
run_verify_case candidate-failed reject
run_verify_case unhealthy reject
run_verify_case task-inspect-error reject
run_verify_case service-inspect-error reject
run_verify_case unstable reject
run_verify_case success reject ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff

if grep -Eq 'staging_gateway_(external_gate_recover|forward_apply)' "$cutover"; then
  echo 'security-cutover helper references normal retry/recovery authority' >&2
  exit 1
fi

echo 'staging gateway security-cutover tests: PASS (2 positive, 16 adversarial negatives)'
