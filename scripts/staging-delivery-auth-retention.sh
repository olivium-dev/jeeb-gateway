#!/usr/bin/env bash
# Staging retention only: no activation, credential reads, or mutations.
# Paired activation must hold a shared gateway/delivery lock, in addition to the
# gateway deployment lock/CAS. An absent posture is not durable activation history.
staging_delivery_auth_snapshot() {
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
staging_delivery_auth_assert_retained() {
  local before=$1 role=$2 spec=$3 after
  after=$(staging_delivery_auth_snapshot "$role" "$spec") || return 1
  [ "$before" = "$after" ] || {
    echo "Staging delivery auth posture changed during deployment" >&2
    return 1
  }
}
