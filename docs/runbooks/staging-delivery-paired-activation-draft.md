# Paired delivery activation transaction skeleton

The original bounded ordering skeleton is retained and reused by the runner-side
runtime adapters. See [the runtime contract](staging-delivery-paired-runtime.md)
for current source custody, receipt, provisioning and readiness details. Neither
this skeleton nor its synthetic ordering tests establish a deployed result.

The skeleton requires every callback to be present and reuses
`staging_gateway_security_cutover_forward_apply` for each fixed role's complete
Spec and captured ID/version. HTTP success alone is insufficient. An uncertain
first submission forbids the second.

The runner holds the canonical shared staging lock through all phases. The strict
holder preserves its inode/protocol without truncation, symlink following or stale
owner overwrite. It does not clear history.

The mandatory sequence is:

1. Protected-source/receipt, exact daemon, existing credential and narrow
   candidate validation; exclusive durable journal begin.
2. Durable gateway-pending record, one gateway CAS, exact runtime and mounted
   credential proof, durable gateway-verified record.
3. Fresh read-only delivery schema attestation against the unchanged
   receipt-bound incumbent, then durable delivery-pending record and one CAS.
4. Exact delivery proof, authenticated gateway-handler readiness and negative
   authentication controls; finally re-inspect both services before complete.

The phase history is:

`prepared → gateway-submission-pending → gateway-verified → delivery-submission-pending → delivery-verified → complete`

No rollback, compensating write, automatic repeated POST, journal reset or history
removal is provided. Schema failure after gateway readiness leaves gateway
verified but delivery untouched. Final dual-service proof failure cannot mark the
transaction complete. A failed CAS does not imply traffic was frozen.

The cross-repository contract is an explicitly reviewed delivery identity and a
short-lived one-use receipt issued by delivery's own protected workflow. Gateway
does not claim private delivery-head access through its own repository token.
