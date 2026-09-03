#!/usr/bin/env bash
# Prints the incumbent staging gateway's persisted FeatureFlags__UseUpstream__Chat
# value (true|false), or nothing when no service or no such row exists.
set -euo pipefail

service=${1:?service name is required}

environment=$(docker service inspect "$service" \
  --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' \
  2>/dev/null) || environment=''

printf '%s\n' "$environment" | awk '
  $0 == "FeatureFlags__UseUpstream__Chat=true"  { print "true";  exit }
  $0 == "FeatureFlags__UseUpstream__Chat=false" { print "false"; exit }'
