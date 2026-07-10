---
name: sprint-002-reuse
description: Reuse findings for Sprint 002 — jeeber availability toggle and request-discovery feed. GAP-1 is a gateway route mismatch; GAP-2 needs an offer-service read endpoint.
metadata:
  type: project
---

Sprint 002 reuse research completed 2026-06-28. Artifact: /Users/oudaykhaled/jeeb-workspace/docs/sprints/sprint-002/legacy-research.md

GAP-1 (availability toggle): The gateway AvailabilityController exists at `PATCH /jeebers/me/availability` but the mobile calls `PUT /v1/jeebers/me/availability`. Fix: change [Route] to include `v1/` prefix and add [HttpPut]. Downstream delivery-service POST /api/v1/jeebers/{id}/availability is live and working. geolocation-service also has a richer implementation (auto-offline, GPS-coupled) as fast-follow.

**Why:** Route mismatch — wrong HTTP verb (PATCH vs PUT) and missing v1/ prefix. No service change needed.

**How to apply:** Whenever availability 404 is reported, check route verb and path prefix in AvailabilityController.cs first before assuming the downstream service is broken.

GAP-2 (request-discovery feed): No jeeber-facing request list exists anywhere. The model is push-based (matching sends push notifications). The offer-service (Elixir) has the data at offers.jeeber_id but lacks a GET /api/v1/jeebers/:id/offers read route. The gateway needs GET /v1/jeebers/me/feed. ~80 lines total across two services.

**Why:** Matching model is push-based; nobody built a poll-based jeeber feed because jeebers were expected to act on push notifications only.

**How to apply:** Any new "jeeber sees requests" feature should extend offer-service (add read route) + gateway (new BFF controller). Do not build a new service.
