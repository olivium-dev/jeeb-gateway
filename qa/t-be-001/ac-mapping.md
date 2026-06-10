# AC mapping — JEB-37 / T-BE-001 → JEB-467 scaffolded tests

Each row maps ONE acceptance criterion (verbatim from JEB-37 description or audit comment #14769/#14764) to ONE xUnit test method. AC × test pairs are one-to-many — a single test method may cover multiple ACs.

**Source of truth precedence**: Audit comment #14769 (orchestrator reconciliation) > LEAD comment #14764 > UX #14761 > PO #14757 > original JEB-37 description.

| AC ID | AC text (verbatim) | Test method | File | Notes |
|---|---|---|---|---|
| **AC1** | "Given a valid Lebanese phone (+961, E.164), When `POST /v1/auth/otp/request`, Then 200 with `{ttlSeconds:300}` (read from downstream `ExpiresAt`) and an SMS arrives within 30s in the staging sandbox." | `OtpRequestTests.ValidLebanesePhone_ReturnsTtl300_AndCallsDownstreamWithLoginPurpose` | `Otp/OtpRequestTests.cs` | SMS-arrival 30s assertion is out of scope for QA-PRE (requires real Twilio sandbox; QA-POST JEB-469 owns it). xUnit asserts the synchronous contract: 200, ttl=300, downstream called with purpose=login. |
| **AC1** | (same as above) | `OtpTtlExpiryTests.OtpJustBeforeExpiry_IsAccepted` | `Otp/OtpTtlExpiryTests.cs` | TTL-boundary positive: at 299s the OTP is still valid → 200. |
| **AC1** | (same as above) | `OtpTtlExpiryTests.OtpAfterTtl_Returns401InvalidOtp` | `Otp/OtpTtlExpiryTests.cs` | TTL-boundary negative: at 301s the OTP is expired → 401 invalid_otp (per AC-ProblemTypeSet; no `expired` type exists in the frozen set). |
| **AC2** | "Given a valid OTP code, When `POST /v1/auth/otp/verify`, Then 200 with JWT pair AND a `user-management` record exists for that phone." | `OtpVerifyTests.CorrectOtp_ReturnsJwtPair_AndFindOrCreatesUser` | `Otp/OtpVerifyTests.cs` | Assert: 200, JWT pair, user-management `find-or-create` called exactly once with normalized phone, sub claim = returned userId. |
| **AC3** | "Given a wrong code, When `POST /v1/auth/otp/verify`, Then 401 ProblemDetails `type=invalid_otp`." | `OtpVerifyTests.WrongOtp_Returns401_InvalidOtpProblem` | `Otp/OtpVerifyTests.cs` | Negative path: no tokens, no user-mgmt call. |
| **AC4** | "Given 3 consecutive wrong attempts against the same active OTP (downstream cap, no time window), When `POST /v1/auth/otp/verify`, Then 429 ProblemDetails `type=too_many_attempts`. Recovery: client requests a new OTP (which resets the downstream attempt counter). Lockout is NOT time-based." | `OtpAttemptCapTests.ThreeWrongAttempts_Then4thReturns429TooManyAttempts` | `Otp/OtpAttemptCapTests.cs` | Asserts 401 invalid_otp for attempts 1-3 and 429 too_many_attempts for the 4th. |
| **AC4** | (same as above) | `OtpAttemptCapTests.ResendAfter429_AcceptsNewOtp` | `Otp/OtpAttemptCapTests.cs` | Recovery path: Resend zeros the counter and a new OTP is accepted. |
| **AC4** | (same as above) | `OtpAttemptCapTests.LockoutPersists_WithoutResend_AfterWaiting` | `Otp/OtpAttemptCapTests.cs` | Negative: waiting 5 min without Resend never recovers. |
| **AC4** | (same as above) | `CrossStoryMobileContractTests.MobileFlow_FullSequence_BackendContractHolds` | `Otp/CrossStoryMobileContractTests.cs` | Cross-story replay of T-MOB-004 AC4 mobile state machine — full request sequence + Resend recovery. `[Trait("CrossStory","T-MOB-004#AC4")]`. |
| **AC5** *(reconciled by JEB-1480 / GR1)* | "JWT access token is **HS256**; signing key ≥32 bytes loaded from env; NEVER from `appsettings.json`." | `JwtAlgorithmReconciliationTests.AccessToken_IsSignedWith_HS256` | `Tokens/JwtAlgorithmReconciliationTests.cs` | **HS512→HS256 reconciliation:** the original JEB-37 AC mandated HS512, but the live `TokenService` mints (and the whole fleet validates) **HS256** from the shared `Jwt:SigningKey`. Switching the live path to HS512 would invalidate every token already issued by the HS256 path — a breaking change (GR1). JEB-1480 therefore drops the HS512 mandate and standardises on the live HS256, keeping a ≥32-byte key requirement. Asserts the minted access-token header `alg == HS256`, that the token validates against the same key, and that a <32-byte key is rejected at construction. |
| **AC5b** | "Refresh-token rotation: each `POST /v1/auth/refresh` issues a new refresh token and revokes the prior one (refresh-token-family pattern). Detected reuse of a revoked refresh token revokes the whole family and forces re-OTP. Access TTL = 1h; refresh TTL = 30d." | `RefreshTokenRotationTests.Refresh_RotatesRefreshToken_InvalidatesPrevious` | `Otp/RefreshTokenRotationTests.cs` | Rotation: N → N+1; replay of N → 401. |
| **AC5b** | (same as above) | `RefreshTokenRotationTests.ReplayRevokedToken_RevokesEntireFamily` | `Otp/RefreshTokenRotationTests.cs` | Replay revokes the whole family; legit N+1 also dies. |
| **AC5b** | (same as above) | `RefreshTokenRotationTests.TokenLifetimes_MatchPolicy` | `Otp/RefreshTokenRotationTests.cs` | exp = 1h (access) ± 60s, 30d (refresh) ± 60s. |
| **AC5b** | (same as above) | `RefreshTokenRotationTests.AfterFamilyRevocation_NewOtpSignInWorks` | `Otp/RefreshTokenRotationTests.cs` | Recovery path: after family revocation a brand-new OTP sign-in works. |
| **AC5b** | (same as above) | `OtpVerifyTests.CorrectOtp_ReturnsJwtPair_AndFindOrCreatesUser` | `Otp/OtpVerifyTests.cs` | Asserts initial-issue token TTLs match policy. |
| **AC6** | "Traces propagate W3C Trace Context end-to-end; `correlationId` echoes in response header." | `OtpRequestTests.Response_EchoesCorrelationIdHeader` | `Otp/OtpRequestTests.cs` | Asserts the `X-Correlation-Id` request header is echoed on the response. W3C TraceContext propagation through downstream clients is asserted indirectly by `PhonePiiScrubberTests.NoOTelSpanAttribute_ContainsRawPhoneSubstring` which requires that at least one OTel span exists. |
| **AC7** | "p99 verify ≤ 800 ms in staging (per NFR-1)." | (out of QA-PRE scope) | n/a | Perf assertion belongs in QA-POST (JEB-469) using K6/NBomber against a staging instance. QA-PRE Schemathesis config nonetheless pins `hypothesis-deadline=2000ms` (2.5× the SLO) so any catastrophic regression surfaces in fuzz. |
| **AC8** | "Schemathesis run against the OpenAPI spec passes for both endpoints." | `qa/t-be-001/schemathesis-runner.sh` | `qa/t-be-001/` | Fuzz runner: 200 examples × 2000 ms deadline × both endpoints, `--checks all` minus the rate-limit caveat documented in the runner header. |
| **AC-PhoneNorm** *(new, from audit #14769)* | "Given an inbound phone in any format, When the gateway processes it, Then it is normalized to E.164 (`libphonenumber-csharp`, region=LB). Any number whose RegionCode != LB is rejected with 400 ProblemDetails `type=invalid_country`." | `PhoneNormalizationTests.EquivalentFormats_AllNormaliseToSameE164` | `Otp/PhoneNormalizationTests.cs` | Theory: 6 equivalent LB formats → identical E.164 + RegionCode=LB. |
| **AC-PhoneNorm** | (same as above) | `PhoneNormalizationTests.NonLebaneseNumbers_ReturnNonLBRegionCode` | `Otp/PhoneNormalizationTests.cs` | Theory: US/FR/GB/SA all → RegionCode ≠ LB, must be rejected with type=invalid_country. |
| **AC-PhoneNorm** | (same as above) | `PhoneNormalizationTests.UnparseableInput_RaisesNumberParseException` | `Otp/PhoneNormalizationTests.cs` | Theory: garbage input raises `NumberParseException` → gateway surfaces `Problem(type=invalid_phone)`. |
| **AC-PhoneNorm** | (same as above) | `OtpRequestTests.NonLebanesePhone_Returns400_InvalidCountryProblem` | `Otp/OtpRequestTests.cs` | End-to-end via HTTP: US number → 400 + type=invalid_country, downstream NOT called. |
| **AC-PhoneNorm** | (same as above) | `OtpRequestTests.UnparseablePhone_Returns400_InvalidPhoneProblem` | `Otp/OtpRequestTests.cs` | End-to-end via HTTP: garbage → 400 + type=invalid_phone. |
| **AC-PhoneNorm** | (same as above) | `OtpRequestTests.ValidLebanesePhone_ReturnsTtl300_AndCallsDownstreamWithLoginPurpose` | `Otp/OtpRequestTests.cs` | Positive: pre-normalization input (`+961 79 123 456`) → downstream sees normalized form (`+96179123456`). |
| **AC-GatewayRateLimit** *(new, from audit #14769)* | "`POST /v1/auth/otp/request` is rate-limited at the gateway: 10 requests / minute / source-IP AND 3 requests / minute / normalized-phone. Excess returns 429 ProblemDetails `type=rate_limited`." | `GatewayRateLimitTests.PerPhone_FourthRequestInWindow_Returns429RateLimited` | `Otp/GatewayRateLimitTests.cs` | Per-phone cap: 3/min → 4th = 429 + Retry-After header. |
| **AC-GatewayRateLimit** | (same as above) | `GatewayRateLimitTests.PerPhone_AfterWindow_RequestSucceedsAgain` | `Otp/GatewayRateLimitTests.cs` | Window slides — after 61s the budget restores. |
| **AC-GatewayRateLimit** | (same as above) | `GatewayRateLimitTests.PerIp_EleventhRequestInWindow_Returns429RateLimited` | `Otp/GatewayRateLimitTests.cs` | Per-IP cap: 10/min using distinct phones → 11th = 429. |
| **AC-GatewayRateLimit** | (same as above) | `GatewayRateLimitTests.RateLimitedRequest_DoesNotCallDownstream` | `Otp/GatewayRateLimitTests.cs` | Resource-protection: rate-limited request must NOT trigger downstream `SendAsync` (SMS-pumping defence). |
| **AC-PhonePIIHash** *(new, from audit #14769; revised by PR #32 review B1)* | "No raw phone is written to logs, OTel traces/metrics, structured events, or downstream span attributes. The only persisted/observed form is `phone.hash = HMAC-SHA256(JeebJwt:PhonePepper, normalizedE164)` (base64url, `ph1:` prefix). Verified by QA-PRE (JEB-467) via log + trace exporter scrape." | `PhonePiiScrubberTests.NoLogRecord_ContainsRawPhoneSubstring` | `Otp/PhonePiiScrubberTests.cs` | Negative: scan every captured log record (message, structured KVs, scopes) for `+961` or subscriber digits. |
| **AC-PhonePIIHash** | (same as above) | `PhonePiiScrubberTests.NoOTelSpanAttribute_ContainsRawPhoneSubstring` | `Otp/PhonePiiScrubberTests.cs` | Negative: scan every emitted OTel span's tags. |
| **AC-PhonePIIHash** | (same as above) | `PhonePiiScrubberTests.PhoneHash_IsPresent_InAtLeastOneSinkRecord` | `Otp/PhonePiiScrubberTests.cs` | Positive: at least one log record OR span tag carries the `ph1:` HMAC marker — proves the hash IS recorded, not just "log nothing". |
| **AC-PhonePIIHash** | (same as above) | `PhonePiiScrubberTests.PhoneHash_IsDeterministic_AcrossRequests` | `Otp/PhonePiiScrubberTests.cs` | PR #32 review B1: two requests for the same phone → same hash. Catches a regression to bcrypt-on-phone (random salt → no correlation). |
| **AC-PhonePIIHash** | (same as above) | `PhonePiiScrubberTests.ProblemDetailsBody_DoesNotEchoRawPhone` | `Otp/PhonePiiScrubberTests.cs` | 4xx ProblemDetails body must not echo `+961` or subscriber digits. |
| **AC-PhonePIIHash** | (same as above) | `PhoneNormalizationTests.EquivalentFormats_AllProduceSameHmacHash` | `Otp/PhoneNormalizationTests.cs` | Theory: equivalent formats → identical HMAC-SHA256 hash (proves normalisation precedes hashing AND hashes are deterministic). |
| **AC-PhonePIIHash** | (same as above) | `OtpVerifyTests.SuccessResponse_DoesNotEchoRawPhone` | `Otp/OtpVerifyTests.cs` | Success body PII check. |
| **AC-PhonePIIHash** | (same as above) | `OtpRequestTests.ValidLebanesePhone_ReturnsTtl300_AndCallsDownstreamWithLoginPurpose` | `Otp/OtpRequestTests.cs` | Success body PII check on /otp/request. |
| **AC-ProblemTypeSet** *(new, from audit #14769; extended by PR #32 review S1/S3)* | "The gateway only emits `type` strings from `OtpProblemTypes.FrozenSet`: `invalid_otp`, `too_many_attempts`, `invalid_country`, `rate_limited`, `invalid_phone`, `service_unavailable` (S1, replaces ad-hoc `/downstream` + `/user_mgmt_unavailable`), `invalid_refresh_token` (S3, replaces `invalid_otp` on the /refresh path). Schemathesis enforces." | `CrossStoryMobileContractTests.EveryReturnedType_IsInTheFrozenSet` | `Otp/CrossStoryMobileContractTests.cs` | Reads `OtpProblemTypes.FrozenSet` directly so the test follows additive extensions to the frozen set without manual edits. |
| **AC-ProblemTypeSet** | (same as above) | `qa/t-be-001/schemathesis-runner.sh` + `openapi-fragment.yaml` | `qa/t-be-001/` | The OpenAPI `ProblemDetails.type` enum is the canonical frozen set; Schemathesis `response_schema_conformance` fails the build on drift. |
| **AC-ProblemTypeSet** | (same as above) | `OtpRequestTests.NonLebanesePhone_Returns400_InvalidCountryProblem` | `Otp/OtpRequestTests.cs` | Per-type verification: `invalid_country`. |
| **AC-ProblemTypeSet** | (same as above) | `OtpRequestTests.UnparseablePhone_Returns400_InvalidPhoneProblem` | `Otp/OtpRequestTests.cs` | Per-type verification: `invalid_phone`. |
| **AC-ProblemTypeSet** | (same as above) | `OtpVerifyTests.WrongOtp_Returns401_InvalidOtpProblem` | `Otp/OtpVerifyTests.cs` | Per-type verification: `invalid_otp`. |
| **AC-ProblemTypeSet** | (same as above) | `OtpAttemptCapTests.ThreeWrongAttempts_Then4thReturns429TooManyAttempts` | `Otp/OtpAttemptCapTests.cs` | Per-type verification: `too_many_attempts`. |
| **AC-ProblemTypeSet** | (same as above) | `GatewayRateLimitTests.PerPhone_FourthRequestInWindow_Returns429RateLimited` | `Otp/GatewayRateLimitTests.cs` | Per-type verification: `rate_limited`. |
| **AC-FINAL** *(process)* | "Before moving the Jira issue to Done, post a Jira comment containing... (a) commit SHA(s) and PR link(s) (b) screenshot or 30 s screen recording proving each AC ..." | (process — owned by AC-FINAL gate, NOT a unit test) | n/a | This AC is procedural; verified by the orchestrator + a custom Jira field `AC-Comment-Posted`. Not in scope for unit-test scaffolding. |

## Cross-references

- **JEB-1422** (`T-BE-001a-user-mgmt-phone-identity`): provides `POST /api/users/phone-identity/find-or-create`. We mock at the typed-client seam via `FakeUserManagementClient`; assertions on the request body and response shape match the contract pinned in audit comment #14764.
- **T-MOB-004 (JEB-8)** AC4 / AC9: `CrossStoryMobileContractTests` carries `[Trait("CrossStory","T-MOB-004#AC4")]` and replays the exact request sequence the mobile state machine emits.
- **JEB-469** (QA-POST): owns p99 latency assertion (AC7), SMS-arrival-within-30s (AC1), BOLA fuzz, real `user-management` integration after JEB-1422 lands.

## Test counts

| File | Test method count |
|---|---|
| `OtpRequestTests.cs` | 4 |
| `OtpVerifyTests.cs` | 3 |
| `OtpAttemptCapTests.cs` | 3 |
| `OtpTtlExpiryTests.cs` | 3 |
| `RefreshTokenRotationTests.cs` | 4 |
| `PhoneNormalizationTests.cs` | 4 (2 theories × multi-row + 2 single-input theories) |
| `PhonePiiScrubberTests.cs` | 4 |
| `GatewayRateLimitTests.cs` | 4 |
| `CrossStoryMobileContractTests.cs` | 2 |
| `PrReviewBlockerTests.cs` | 10 (B1 / B3 / S3 regression) |
| **Total** | **41 test methods** (≥ 60 invocations when xUnit theories expand) |

## PR #32 review resolution

| Severity | Item | Resolution | Test |
|---|---|---|---|
| B1 | bcrypt phone hash generates random salt → no correlation | Replaced `BcryptPhoneHasher` with `HmacShaPhoneHasher` (HMAC-SHA256 + `JeebJwt:PhonePepper`). Pepper is `[Required]`, env-only, dev-only guard mirrors `SigningKey`. | `PrReviewBlockerTests.HmacPhoneHasher_DeterministicAcrossInstances`, `PhonePiiScrubberTests.PhoneHash_IsDeterministic_AcrossRequests` |
| B2 | In-memory rate limiter scales with pods; no `UseForwardedHeaders` | Added `Configure<ForwardedHeadersOptions>` + `app.UseForwardedHeaders()` with `KnownProxies` / `KnownNetworks` allowlist from config. Added startup warning when non-Development and `GatewayRateLimit:RedisConnectionString` is unset. TODO recorded for the Redis-backed limiter swap. | Wiring-only; covered by code review |
| B3 | Dev-shaped signing key satisfies MinLength(64) with no production guard | `AddJeebOtpSignIn` now takes `IHostEnvironment`; ValidateOnStart refuses any `SigningKey` (and `PhonePepper`) starting with `dev-only-` in non-Development. | `PrReviewBlockerTests.Production_DevOnlySigningKey_FailsStart`, `Staging_DevOnlySigningKey_FailsStart`, `Development_DevOnlySigningKey_StartsSuccessfully`, `Production_RealSigningKey_StartsSuccessfully` |
| S1 | `/downstream` + `/user_mgmt_unavailable` not in frozen set | Consolidated into `service_unavailable` (added to `OtpProblemTypes.FrozenSet`). | `CrossStoryMobileContractTests.EveryReturnedType_IsInTheFrozenSet` (now reads `FrozenSet` directly) |
| S3 | Refresh path emits `invalid_otp` for refresh-token failures | Added `invalid_refresh_token` to the frozen set; refresh path now emits it on missing / invalid / replayed tokens. | `PrReviewBlockerTests.Refresh_MissingToken_Returns_InvalidRefreshToken`, `Refresh_GarbageToken_Returns_InvalidRefreshToken`, `Refresh_ReplayedToken_Returns_InvalidRefreshToken` |
