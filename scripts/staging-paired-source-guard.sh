#!/usr/bin/env bash
set -euo pipefail
[ "${GITHUB_REPOSITORY:-}" = olivium-dev/jeeb-gateway ]
[ "${GITHUB_ACTOR:-}" = oudaykhaled ]
[ "${GITHUB_TRIGGERING_ACTOR:-}" = oudaykhaled ]
[ "${GITHUB_EVENT_NAME:-}" = workflow_dispatch ]
[ "${GITHUB_REF:-}" = refs/heads/main ]
[ "${GITHUB_REF_PROTECTED:-}" = true ]
[[ "${GITHUB_SHA:-}" =~ ^[0-9a-f]{40}$ ]]
[ "$(git rev-parse HEAD)" = "$GITHUB_SHA" ]
gh api repos/olivium-dev/jeeb-gateway/branches/main \
  --jq '.protected == true and .commit.sha == "'"$GITHUB_SHA"'"' | grep -Fxq true
gh api "repos/olivium-dev/jeeb-gateway/actions/workflows/forward-only-authority-audit.yml/runs?branch=main&event=push&head_sha=$GITHUB_SHA&per_page=100" \
  --jq '[.workflow_runs[] | select(.head_sha == "'"$GITHUB_SHA"'" and .path == ".github/workflows/forward-only-authority-audit.yml")]
    | sort_by(.id) | last | .status == "completed" and .conclusion == "success"' | grep -Fxq true
