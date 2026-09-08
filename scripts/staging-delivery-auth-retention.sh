#!/usr/bin/env bash
# Staging retention only: no activation, credential reads, or mutations.
# Paired activation must hold a shared gateway/delivery lock, in addition to the
# gateway deployment lock/CAS. An absent posture is not durable activation history.
staging_delivery_auth_snapshot_unjournaled() {
  local role=$1 spec=$2 projection secret_id metadata
  case "$role" in gateway|delivery) ;; *) return 64 ;; esac
  projection=$(jq -ceS --arg role "$role" '
    def fail: error("invalid staging delivery auth posture");
    .TaskTemplate.ContainerSpec as $c
    | [($c.Env // [])[] | select((split("=")[0] | ascii_downcase | gsub("__"; ":"))
        | . == "delivery_service_token" or . == "delivery_service_token_file"
          or . == "delivery_service_auth_mode" or . == "services:delivery:servicetoken"
          or . == "services:delivery:servicetokenfile")] as $rows
    | [$rows[] | select(startswith("DELIVERY_SERVICE_TOKEN_FILE="))] as $files
    | [$rows[] | select(startswith("DELIVERY_SERVICE_AUTH_MODE="))] as $modes
    | [($c.Secrets // [])[] | select(.File.Name == "delivery_service_token"
        or .File.Name == "/run/secrets/delivery_service_token"
        or .SecretName == "jeeb-staging-delivery-service-auth-v1")] as $mounts
    | [($c.Env // [])[] | select(startswith("SKIP_DB_INIT="))] as $skip
    | if ([($c.Mounts // [])[] | select(.Target == "/run/secrets"
            or .Target == "/run/secrets/delivery_service_token")]
          + [($c.Configs // [])[] | select(.File.Name == "delivery_service_token"
            or .File.Name == "/run/secrets/delivery_service_token")] | length) > 0 then fail
      elif ($rows | length) == 0 and ($mounts | length) == 0 then
        {active:false}
      else
        (if $role == "gateway" then "65532" else "0" end) as $uid
        | if $files != ["DELIVERY_SERVICE_TOKEN_FILE=/run/secrets/delivery_service_token"]
          or ($modes != (if $role == "delivery" then ["DELIVERY_SERVICE_AUTH_MODE=required"]
                        else [] end))
          or ($rows | length) != (if $role == "delivery" then 2 else 1 end)
          or ($mounts | length) != 1
          or $mounts[0].SecretName != "jeeb-staging-delivery-service-auth-v1"
          or ($mounts[0].SecretID | type) != "string"
          or ($mounts[0].SecretID | test("^[a-z0-9]+$") | not)
          or $mounts[0].File != {Name:"delivery_service_token",UID:$uid,GID:$uid,Mode:256}
          or ($role == "delivery" and $skip != ["SKIP_DB_INIT=true"])
          then fail
          else {active:true,mount:$mounts[0],file:$files,mode:$modes,
                skip:(if $role == "delivery" then $skip else [] end)}
          end
      end
  ' "$spec") || return 1
  if [ "$(jq -r .active <<< "$projection")" = true ]; then
    secret_id=$(jq -r .mount.SecretID <<< "$projection")
    metadata=$(docker secret inspect "$secret_id") || return 1
    jq -e --arg id "$secret_id" '
      length == 1 and .[0].ID == $id
      and .[0].Spec.Name == "jeeb-staging-delivery-service-auth-v1"
      and .[0].Spec.Labels["jeeb.environment"] == "staging"
      and .[0].Spec.Labels["jeeb.purpose"] == "delivery-service-auth"
      and .[0].Spec.Labels["jeeb.version"] == "1"
    ' <<< "$metadata" >/dev/null || {
      echo "Invalid staging delivery auth secret metadata" >&2
      return 1
    }
  fi
  printf '%s\n' "$projection"
}
staging_delivery_auth_history_secret_id() {
  python3 - <<'PY'
import json, os, re, stat, sys
from pathlib import Path
opened = []
try:
    home = Path.home()
    def guard(value):
        if not value: raise ValueError()
    def pairs(rows):
        result = {}
        for key, value in rows:
            guard(key not in result)
            result[key] = value
        return result
    guard(home.is_absolute() and home.resolve() == home)
    directory = os.open('/', os.O_RDONLY | os.O_DIRECTORY)
    opened.append(directory)
    for part in home.parts[1:]:
        directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
        opened.append(directory)
        info = os.fstat(directory)
        guard(info.st_uid in (0, os.getuid()) and not info.st_mode & 0o022)
    guard(os.fstat(directory).st_uid == os.getuid())
    for name in ('.jeeb-deploy', 'paired-releases', 'jeeb-staging-delivery-service-auth-v1'):
        try:
            directory = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
        except FileNotFoundError:
            print('absent')
            sys.exit(0)
        opened.append(directory)
        info = os.fstat(directory)
        guard(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700)
    phases = ('prepared', 'gateway-submission-pending', 'gateway-verified', 'delivery-submission-pending', 'delivery-verified', 'complete')
    guard(set(os.listdir(directory)) == {f'{i:02d}-{phase}.json' for i, phase in enumerate(phases)})
    baseline = None
    for i, phase in enumerate(phases):
        fd = os.open(f'{i:02d}-{phase}.json', os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        try:
            info = os.fstat(fd)
            guard(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o400 and info.st_nlink == 1 and info.st_size <= 32768)
            with os.fdopen(fd, closefd=False) as stream: value = json.load(stream, object_pairs_hook=pairs)
        finally: os.close(fd)
        guard(value.pop('phase') == phase)
        if baseline is None: baseline = value
        guard(value == baseline)
    guard(re.fullmatch(r'[a-z0-9]{25}', baseline['secretId']))
    allowed = {'secretId', 'receiptNonce', 'gatewayBuildRun', 'gatewayBuildAttempt'}
    for key in ('gatewayBuildRun', 'gatewayBuildAttempt'): guard(re.fullmatch(r'[1-9][0-9]*', baseline[key]))
    for role, repository in (('gateway', 'jeeb-gateway'), ('delivery', 'delivery-service')):
        allowed.update(role + suffix for suffix in ('Source', 'Tree', 'Image', 'Run', 'Attempt', 'ServiceId', 'Version'))
        for suffix in ('Source', 'Tree'): guard(re.fullmatch(r'[0-9a-f]{40}', baseline[role + suffix]))
        for suffix in ('Run', 'Attempt'): guard(re.fullmatch(r'[1-9][0-9]*', baseline[role + suffix]))
        guard(re.fullmatch(r'ghcr\.io/olivium-dev/' + repository + r'@sha256:[0-9a-f]{64}', baseline[role + 'Image']))
        guard(re.fullmatch(r'[a-z0-9]{25}', baseline[role + 'ServiceId']))
        guard(type(baseline[role + 'Version']) is int and baseline[role + 'Version'] > 0)
    guard(set(baseline) == allowed and re.fullmatch(r'[0-9a-f]{64}', baseline['receiptNonce']))
    print(baseline['secretId'])
except Exception:
    print('Staging delivery activation history requires reconciliation.', file=sys.stderr)
    sys.exit(1)
finally:
    for fd in reversed(opened): os.close(fd)
PY
}
staging_delivery_auth_snapshot() {
  local role=$1 spec=$2 history projection peer_service peer_spec peer_projection
  history=$(staging_delivery_auth_history_secret_id) || return 1
  projection=$(staging_delivery_auth_snapshot_unjournaled "$role" "$spec") || return 1
  if [ "$history" != absent ]; then
    jq -e --arg id "$history" '.active == true and .mount.SecretID == $id' <<< "$projection" >/dev/null || return 1
    case "$role" in
      gateway) peer_service=jeeb-staging-delivery-service ;;
      delivery) peer_service=jeeb-staging-jeeb-gateway ;;
      *) return 1 ;;
    esac
    peer_spec=$(docker service inspect "$peer_service" --format '{{json .Spec}}') || return 1
    if [ "$role" = gateway ]; then
      peer_projection=$(printf '%s' "$peer_spec" | staging_delivery_auth_snapshot_unjournaled delivery /dev/stdin) || return 1
    else
      peer_projection=$(printf '%s' "$peer_spec" | staging_delivery_auth_snapshot_unjournaled gateway /dev/stdin) || return 1
    fi
    jq -e --arg id "$history" '.active == true and .mount.SecretID == $id' <<< "$peer_projection" >/dev/null || return 1
  fi
  printf '%s\n' "$projection"
}
staging_delivery_auth_assert_retained() {
  local before=$1 role=$2 spec=$3 after
  after=$(staging_delivery_auth_snapshot "$role" "$spec") || return 1
  [ "$before" = "$after" ] || {
    echo "Staging delivery auth posture changed during deployment" >&2
    return 1
  }
}
