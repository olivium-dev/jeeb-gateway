#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo '::error::usage: reject-staging-gateway-alias.sh SERVICE' >&2
  exit 64
fi

requested_service=$1
case "$requested_service" in
  ''|*[!a-zA-Z0-9_.-]*)
    echo '::error::service_name contains unsupported characters' >&2
    exit 1
    ;;
esac

# Resolve aliases and Swarm IDs before registry login or any other mutation.
# An unavailable or malformed authority is never interpreted as "not staging".
if ! canonical_service=$(ssh jeeb \
  "docker service inspect '$requested_service' --format '{{.Spec.Name}}'"); then
  echo '::error::Unable to resolve the canonical Swarm service; refusing deployment' >&2
  exit 1
fi
case "$canonical_service" in
  ''|*$'\n'*|*[!a-zA-Z0-9_.-]*)
    echo '::error::Canonical Swarm service resolution returned an invalid name' >&2
    exit 1
    ;;
esac
if [ "$canonical_service" = jeeb-staging-jeeb-gateway ]; then
  echo '::error::The staging gateway can be changed only by staging-authorized workflows' >&2
  exit 1
fi
