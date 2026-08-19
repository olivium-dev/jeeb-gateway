#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT
mkdir -p "$TMP_DIR/bin"

readonly IMAGE="ghcr.io/olivium-dev/example@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

cat > "$TMP_DIR/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
mode=${MOCK_MODE:-pass}
image="ghcr.io/olivium-dev/example@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
image_id="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
container_id="cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
case "$*" in
  "service inspect svc --format "{{.ID}}) echo service123 ;;
  "service inspect service123 --format "*TaskTemplate.ContainerSpec.Image*)
    [ "$mode" = mismatch ] && echo ghcr.io/olivium-dev/other@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa || echo "$image"
    ;;
  "service inspect service123 --format "*UpdateStatus*)
    [ "$mode" = paused ] && echo paused || echo completed
    ;;
  "service inspect service123 --format "*Replicated.Replicas*) echo 1 ;;
  "service ps service123 --filter desired-state=running --format "*)
    echo task123
    if [ "$mode" = multiple ]; then echo task456; fi
    ;;
  "inspect task123 --format "*".Status.State"*)
    [ "$mode" = wrong_service ] && echo 'running|running|other' || echo 'running|running|service123'
    ;;
  "inspect task123 --format "*".Spec.ContainerSpec.Image"*) echo "$image" ;;
  "inspect task123 --format "*".Status.ContainerStatus.ContainerID"*) echo "$container_id" ;;
  "image inspect "*" --format "*".Id"*) echo "$image_id" ;;
  "inspect ${container_id} --format "*".Image"*) echo "$image_id" ;;
  *) echo "unexpected mock docker invocation: $*" >&2; exit 90 ;;
esac
MOCK
chmod +x "$TMP_DIR/bin/docker"

PATH="$TMP_DIR/bin:$PATH" "$HERE/verify-swarm-service-image.sh" svc "$IMAGE"

for mode in mismatch paused multiple wrong_service; do
  if MOCK_MODE="$mode" PATH="$TMP_DIR/bin:$PATH" "$HERE/verify-swarm-service-image.sh" \
    svc "$IMAGE" >/dev/null 2>&1; then
    echo "expected verifier to reject $mode state" >&2
    exit 1
  fi
done

for invalid in "ghcr.io/olivium-dev/example:""latest" ghcr.io/olivium-dev/example:commit; do
  if PATH="$TMP_DIR/bin:$PATH" "$HERE/verify-swarm-service-image.sh" svc "$invalid" >/dev/null 2>&1; then
    echo "expected verifier to reject mutable image reference" >&2
    exit 1
  fi
done

echo "verify-swarm-service-image tests: PASS"
