def env_pair: capture("^(?<key>[^=]+)=(?<value>.*)$");
def banned_legacy_host: "192.168.2." + "50";
def canonical_identifier: ascii_downcase | gsub("[^a-z0-9]"; "");
def raw_secret_config_key:
  canonical_identifier as $key
  | if (($key | endswith("file")) or ($key | endswith("path"))) then
      false
    else
      [
        "password",
        "secret",
        "token",
        "signingkey",
        "privatekey",
        "credential",
        "credentials",
        "apikey",
        "jwtkey"
      ]
      | any(. as $suffix | $key | endswith($suffix))
    end;
def embeds_inline_credential:
  ascii_downcase
  | (test("(^|[;?&])\\s*(password|pwd|user ?id|username|uid)\\s*=")
    or test("^[a-z][a-z0-9+.-]*://[^/@[:space:]]+:[^/@[:space:]]+@"));
def forbidden_payment_gateway_reference:
  ascii_downcase
  | test(
      "(^|[^a-z0-9])((unified[-_. ]*payment([-_. ]*gateway)?)|(payment[-_. ]*gateway)|upg)([^a-z0-9]|$)"
    );

(.TaskTemplate.ContainerSpec.Env // []) as $rows
| [$rows[] | env_pair] as $pairs
| ($pairs | map(.key | ascii_downcase)) as $lower_keys
| ($pairs
    | map({key:(.key | ascii_downcase), value:.value})
    | from_entries) as $environment
| .EndpointSpec.Ports as $ports
| .TaskTemplate.Networks as $networks
| (tojson | ascii_downcase) as $candidate_text
| ($lower_keys | length) == ($lower_keys | unique | length)
  and .TaskTemplate.ContainerSpec.Image == $image
  and ($networks == [{Target:$network_id}])
  and ($ports | length) == 1
  and $ports[0].PublishedPort == $published
  and $ports[0].TargetPort == 8080
  and $ports[0].PublishMode == "ingress"
  and .EndpointSpec.Mode == "vip"
  and .Mode.Replicated.Replicas == 1
  and (
    if ($deployment_mode == "normal" or $deployment_mode == "otp-cutover") then
      .UpdateConfig.Order == "start-first"
      and .UpdateConfig.FailureAction == "rollback"
      and .RollbackConfig.Order == "start-first"
      and .RollbackConfig.FailureAction == "pause"
    elif $deployment_mode == "security-cutover" then
      .UpdateConfig.Parallelism == 1
      and .UpdateConfig.Monitor == 20000000000
      and .UpdateConfig.Order == "stop-first"
      and .UpdateConfig.FailureAction == "pause"
      and .RollbackConfig.Parallelism == 1
      and .RollbackConfig.Monitor == 20000000000
      and .RollbackConfig.Order == "stop-first"
      and .RollbackConfig.FailureAction == "pause"
    else
      false
    end
  )
  and $environment["services__serviceotp__baseurl"] == "http://jeeb-staging-one-time-password:8080"
  and $environment["serviceotpapi__baseurl"] == "http://jeeb-staging-one-time-password:8080"
  and $environment["featureflags__useupstream__otp"] == "true"
  and $environment["featureflags__useupstream__chat"] == "false"
  and $environment["featureflags__useupstream__realtime"] == "false"
  and $environment["features__realtimewebsocketproxy__enabled"] == "false"
  and $environment["auth__otp__applicationid"] == "0d51afe1-499f-4a29-a55a-36d2dd223b05"
  and $environment["auth__otp__phone__allowedregion"] == "LB"
  and $environment["auth__otp__phone__enforceregion"] == "false"
  and (
    if $deployment_mode == "otp-cutover" then
      $environment["serviceauth__enabled"] == "true"
      and $environment["serviceauth__caller"] == "jeeb-gateway"
      and $environment["serviceauth__signingkeyfile"] == "/run/secrets/jeeb_gateway_service_auth"
      and any(
        .TaskTemplate.ContainerSpec.Secrets[]?;
        .File.Name == "jeeb_gateway_service_auth"
        and .File.Mode == 256
      )
    else
      true
    end
  )
  and $environment["services__realtime__baseurl"] == "http://jeeb-staging-realtime-comunication-service:4000"
  and $environment["featureflags__useupstream__voice"] == "false"
  # Owner ruling 2026-08-27: the dev APIs — Super Login Plus among them — must be
  # available on STAGING. Still asserted EXACTLY, so the candidate Spec cannot
  # drift or omit them; only the required value moved. Production uses a
  # different deploy path and a different contract.
  and $environment["superlogin__openmode"] == "true"
  and $environment["demousers__enabled"] == "true"
  and ($environment["forwardedheaders__knownproxies__0"]
    | split(".") as $octets
    | ($octets | length) == 4
      and ($octets | all(
        test("^[0-9]{1,3}$")
        and ((tonumber >= 0) and (tonumber <= 255)))))
  and ($candidate_text | contains(banned_legacy_host) | not)
  and ($pairs | all(
    . as $pair
    | ((($pair.key | forbidden_payment_gateway_reference)
      or ($pair.value | forbidden_payment_gateway_reference))
      | not)))
  and ($pairs | all((.key | raw_secret_config_key) | not))
  and ($pairs | all((.value | embeds_inline_credential) | not))
  and ($pairs | all(
    (.value | ascii_downcase) as $value
    | ((($value | contains("192.168.2.20:10037"))
      or ($value | contains("192.168.2.20:10069")))
      | not)))
