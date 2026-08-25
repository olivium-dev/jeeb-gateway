#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
contract="$repository_root/scripts/staging-gateway-candidate-contract.jq"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
candidate="$test_root/candidate.json"
mutant="$test_root/mutant.json"
image=repo@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
network_id=networkabc

cat > "$candidate" <<JSON
{
  "TaskTemplate": {
    "ContainerSpec": {
      "Image": "$image",
      "Env": [
        "Services__ServiceOTP__BaseUrl=http://jeeb-staging-one-time-password:8080",
        "ServiceOTPApi__BaseUrl=http://jeeb-staging-one-time-password:8080",
        "FeatureFlags__UseUpstream__Otp=true",
        "FeatureFlags__UseUpstream__Chat=false",
        "FeatureFlags__UseUpstream__Realtime=false",
        "Auth__Otp__ApplicationId=0d51afe1-499f-4a29-a55a-36d2dd223b05",
        "Auth__Otp__Phone__AllowedRegion=LB",
        "Auth__Otp__Phone__EnforceRegion=true",
        "Services__Realtime__BaseUrl=http://jeeb-staging-realtime-comunication-service:4000",
        "FeatureFlags__UseUpstream__Voice=false",
        "SuperLogin__OpenMode=false",
        "DemoUsers__Enabled=false",
        "ForwardedHeaders__KnownProxies__0=172.18.0.1",
        "Jwt__SigningKeyFile=/run/secrets/jeeb_gateway_jwt",
        "ServiceNotificationClient__ServiceTokenFile=/run/secrets/notification_service_token"
      ]
    },
    "Networks": [{"Target":"$network_id"}]
  },
  "EndpointSpec": {
    "Mode": "vip",
    "Ports": [{"PublishedPort":10000,"TargetPort":8080,"PublishMode":"ingress"}]
  },
  "Mode": {"Replicated":{"Replicas":1}},
  "UpdateConfig": {"Order":"start-first","FailureAction":"rollback"},
  "RollbackConfig": {"Order":"start-first","FailureAction":"pause"}
}
JSON

validate() {
  jq -e --arg image "$image" --arg network_id "$network_id" \
    --argjson published 10000 -f "$contract" "$1" >/dev/null
}

reject_mutant() {
  local description=$1 filter=$2
  jq "$filter" "$candidate" > "$mutant"
  if validate "$mutant"; then
    echo "candidate contract accepted unsafe mutant: $description" >&2
    exit 1
  fi
}

accept_mutant() {
  local description=$1 filter=$2
  jq "$filter" "$candidate" > "$mutant"
  if ! validate "$mutant"; then
    echo "candidate contract rejected safe mutant: $description" >&2
    exit 1
  fi
}

validate "$candidate"
accept_mutant 'unrelated upgrade key and hostname contain a non-token UPG substring' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__UpgradePolicy__BaseUrl=http://upgrade.internal"]'
accept_mutant 'unrelated backup-gateway word contains a non-token UPG substring' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__BackupGateway__BaseUrl=http://backupgateway.internal"]'
reject_mutant 'missing compatibility OTP alias' \
  'del(.TaskTemplate.ContainerSpec.Env[1])'
reject_mutant 'host-port OTP endpoint' \
  '(.TaskTemplate.ContainerSpec.Env[0]) = "Services__ServiceOTP__BaseUrl=http://192.168.2.20:10037"'
reject_mutant 'host-port realtime endpoint' \
  '(.TaskTemplate.ContainerSpec.Env[8]) = "Services__Realtime__BaseUrl=http://192.168.2.20:10069"'
reject_mutant 'Chat activated in A1' \
  '(.TaskTemplate.ContainerSpec.Env[3]) = "FeatureFlags__UseUpstream__Chat=true"'
reject_mutant 'Realtime activated in A1' \
  '(.TaskTemplate.ContainerSpec.Env[4]) = "FeatureFlags__UseUpstream__Realtime=true"'
reject_mutant 'wrong b05 application ID' \
  '(.TaskTemplate.ContainerSpec.Env[5]) = "Auth__Otp__ApplicationId=wrong"'
reject_mutant 'Lebanon enforcement disabled' \
  '(.TaskTemplate.ContainerSpec.Env[7]) = "Auth__Otp__Phone__EnforceRegion=false"'
reject_mutant 'Voice activated' \
  '(.TaskTemplate.ContainerSpec.Env[9]) = "FeatureFlags__UseUpstream__Voice=true"'
reject_mutant 'case-insensitive duplicate environment key' \
  '.TaskTemplate.ContainerSpec.Env += ["DEMousers__enabled=false"]'
reject_mutant 'extra task network' \
  '.TaskTemplate.Networks += [{"Target":"othernetwork"}]'
reject_mutant 'invalid forwarded proxy value' \
  '(.TaskTemplate.ContainerSpec.Env[12]) = "ForwardedHeaders__KnownProxies__0=not-an-ip"'
reject_mutant 'out-of-range forwarded proxy octet' \
  '(.TaskTemplate.ContainerSpec.Env[12]) = "ForwardedHeaders__KnownProxies__0=999.18.0.1"'
reject_mutant 'Super Login opened' \
  '(.TaskTemplate.ContainerSpec.Env[10]) = "SuperLogin__OpenMode=true"'
reject_mutant 'mixed-case unified payment gateway key' \
  '.TaskTemplate.ContainerSpec.Env += ["uNiFiEdPaYmEnTgAtEwAy__BaSeUrL=http://gateway.invalid"]'
reject_mutant 'mixed-case unified payment gateway destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=HTTP://UNIFIED-PAYMENT_GATEWAY:8080"]'
reject_mutant 'exact legacy unified-payment service key' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__UnifiedPayment__BaseUrl=http://gateway.invalid"]'
reject_mutant 'mixed-case payment-gateway service key' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__PaYmEnTgAtEwAy__BaseUrl=http://gateway.invalid"]'
reject_mutant 'mixed-case payment-gateway destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=HTTP://PaYmEnT-GaTeWaY:8080"]'
reject_mutant 'token-boundary UPG service key' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__uPg__BaseUrl=http://gateway.invalid"]'
reject_mutant 'token-boundary mixed-case UPG destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=HTTP://UpG.Internal:8080"]'
reject_mutant 'forbidden .50 destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=http://192.168.2." + "50:10026"]'
reject_mutant 'mixed-case inline JWT signing key' \
  '.TaskTemplate.ContainerSpec.Env += ["jWt__sIgNiNgKeY=not-a-mounted-secret"]'
reject_mutant 'mixed-case inline password' \
  '.TaskTemplate.ContainerSpec.Env += ["Database__PaSsWoRd=not-a-mounted-secret"]'
reject_mutant 'mixed-case connection-string password' \
  '.TaskTemplate.ContainerSpec.Env += ["Database__ConnectionString=Host=db;PASSWORD=not-a-mounted-secret"]'
reject_mutant 'URL-embedded credential' \
  '.TaskTemplate.ContainerSpec.Env += ["Database__ConnectionString=postgres://user:password@db/jeeb"]'

echo 'staging gateway candidate semantic contract tests: PASS (3 positive, 25 negative)'
