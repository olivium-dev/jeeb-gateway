# Staging delivery credential retention

The staging deploy validates an existing dedicated delivery credential without
reading its bytes. Fully absent auth remains permitted before paired activation.
Partial, conflicting, malformed, or unexpected declarations fail closed.

The activated contract is:

- Secret name: `jeeb-staging-delivery-service-auth-v1`.
- Labels: `jeeb.environment=staging`, `jeeb.purpose=delivery-service-auth`,
  `jeeb.version=1`.
- Target: `delivery_service_token`; file declaration:
  `DELIVERY_SERVICE_TOKEN_FILE=/run/secrets/delivery_service_token`.
- Gateway UID/GID: `65532/65532`; delivery UID/GID: `0/0`; mode: `0400`.
- Delivery also retains `DELIVERY_SERVICE_AUTH_MODE=required` and
  `SKIP_DB_INIT=true`.

The exact existing SecretID and entire mount object are captured and compared.
Gateway checks its candidate before its existing locked compare-and-swap update.
Delivery checks before and after its existing CLI update, which preserves
unspecified environment rows and secret mounts. Tests execute the policy using
synthetic Docker metadata and exercise the relevant workflow transformation/update.

This change does not activate auth, create a secret, rotate credentials, update
an image, or change database behavior. Paired activation must separately verify
that both services mount the same SecretID and integrate a shared gateway/delivery
mutation lock. Delivery currently has no cross-service lock or full-spec CAS.
The retention policy cannot detect a wholly removed historical activation from
an otherwise fully inactive live spec; a durable activation record is separate
work for that coordinated activation. These limits must be resolved before
claiming concurrency-safe paired activation.
