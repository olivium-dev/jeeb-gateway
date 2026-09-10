# Processing-only export pause

`Users:DataExport:ProcessingEnabled` defaults to `true`. A deployment can explicitly
set `Users__DataExport__ProcessingEnabled=false` before startup to keep queued
exports untouched during an owner/configuration activation. The singleton policy
is a startup snapshot, not a hot-reload switch; changes require a reviewed restart.

This is separate from `Users:DataExport:Enabled`, which must remain `true` when
the export feature is available. Processing pause does not bypass readiness:
the internal-job, private-artifact and export-signing credential checks remain
armed under their existing conditions. Missing credentials still fail strict
readiness. Do not remove the artifact URL or use feature-disable as a substitute.

While paused:

- Both hosted processors refrain from export claims/packaging. Direct handler
  invocation is also rejected before artifact recovery, signing or notification.
- The authenticated internal export sweep returns503 ProblemDetails with type
  `urn:jeeb:data-export-processing-paused`. It is not a successful empty sweep.
- No export claim, lease, attempt, retry/defer, terminal state or due date is
  changed by processing. Account-deletion execution is unaffected.
- User export requests and status remain available. New requests can queue;
  previously completed exports retain their existing single-use download behavior.

`DurableWorkSweep:Enabled=false` only stops its hosted driver and also affects
account deletion; it does not protect manual sweep calls or the legacy processor.
Use the processing policy for the narrower guarantee.

The pause must be configured on every executor sharing the same state-service
export queue. It cannot cancel an already-running old process or another replica.
Verify old execution has ended before accepting the replacement. The pause does
not extend deadlines or the72-hour SLA: queued work ages normally. Make its limited
maintenance window visible, and require an explicit subsequent activation review
before releasing work. Do not claim real jobs merely to test the pause.

This source change does not deploy an artifact owner, provision credentials,
activate public ingress, authorize real exports or modify queue records. There
is no rollback or automatic resume mechanism.
