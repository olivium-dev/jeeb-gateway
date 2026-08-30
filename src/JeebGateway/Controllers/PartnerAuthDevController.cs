using System;
using JeebGateway.Auth.Capabilities;
using JeebGateway.JeebWallet;
using JeebGateway.Partner.Auth;
using JeebGateway.Security;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace JeebGateway.Controllers;

/// <summary>
/// <b>[DevOnly]</b> partner-credential seed seam for the test harness / local runs:
/// <c>POST /dev/partner/credentials</c>. Lets a scenario provision a partner without a committed
/// config roster, so an end-to-end "log in → top up" flow can run against a fresh host.
///
/// <para><b>Never a production surface.</b> The whole controller carries <see cref="DevOnlyAttribute"/>:
/// when <c>Features:DevEndpoints:Enabled</c> is false (the committed value in EVERY environment,
/// including production) every action 404s — indistinguishable from a route that does not exist. This
/// mirrors <see cref="DevController"/> exactly. Runtime bindings are stored as hashed, expiring
/// records in the shared idempotency store so cleanup and one-shot use survive replica changes.</para>
/// </summary>
[DevOnly]
[ApiController]
[Route("dev/partner")]
[Produces("application/json")]
// Config-gated dev seam ([DevOnly]) — anonymous-by-design, bypasses L2 (mirrors DevController; ADR-005 §A).
[AllowAnonymous]
[PublicEndpoint("Config-gated [DevOnly] partner-credential seed seam — ADR-005 §A public.")]
public sealed class PartnerAuthDevController : ControllerBase
{
    private readonly IPartnerCredentialStore _credentials;
    private readonly IPartnerWalletProvisioner _wallets;
    private readonly ITokenService _tokens;
    private readonly ILogger<PartnerAuthDevController> _log;

    public PartnerAuthDevController(
        IPartnerCredentialStore credentials,
        IPartnerWalletProvisioner wallets,
        ITokenService tokens,
        ILogger<PartnerAuthDevController> log)
    {
        _credentials = credentials;
        _wallets = wallets;
        _tokens = tokens;
        _log = log;
    }

    /// <summary>POST /dev/partner/credentials — provision a one-shot partner login credential at runtime.</summary>
    [HttpPost("credentials")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SeedCredential(
        [FromBody] PartnerAuthDevSeedRequest request,
        CancellationToken ct)
    {
        // [ApiController] 400s missing/blank fields; validate the holder id shape here.
        if (!Guid.TryParse(request.HolderId, out var holderId) || holderId == Guid.Empty)
        {
            return Problem(
                title: "Invalid holderId.",
                detail: "holderId must be a GUID (the partner's user-management userId).",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://jeeb.dev/errors/invalid-holder-id");
        }

        try
        {
            await _credentials.ReserveRuntimeSeedAsync(
                request.Identifier, holderId, request.DisplayName, request.Password, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log.LogWarning(ex, "partner.auth.dev rejected a credential collision before wallet provisioning.");
            return Problem(
                title: "Partner credential conflicts with an existing binding.",
                detail: "Use the same generated identifier and holder for a retry, or create a fresh scenario actor.",
                statusCode: StatusCodes.Status409Conflict,
                type: "https://jeeb.dev/errors/dev-partner-credential-conflict");
        }

        try
        {
            // Do not expose a usable partner login until its real source wallet exists. The
            // subsequent cash-credit and top-up still travel through the audited production APIs.
            await _wallets.EnsureAsync(holderId, request.DisplayName, ct);
            await _credentials.ActivateRuntimeSeedAsync(request.Identifier, holderId, ct);
            _log.LogInformation("partner.auth.dev seeded a partner credential with a ready wallet.");
            return NoContent();
        }
        catch (WalletProvisioningUnavailableException ex)
        {
            _log.LogWarning(ex, "partner.auth.dev could not provision the partner wallet.");
            return Problem(
                title: "wallet-service unavailable",
                detail: "The partner login was not created because its wallet is not ready.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://jeeb.dev/errors/dev-partner-wallet-provisioning");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log.LogWarning(ex, "partner.auth.dev credential binding changed during wallet provisioning.");
            return Problem(
                title: "Partner credential binding changed during provisioning.",
                detail: "The credential was not activated. Create a fresh scenario actor before retrying.",
                statusCode: StatusCodes.Status409Conflict,
                type: "https://jeeb.dev/errors/dev-partner-credential-conflict");
        }
    }

    [HttpDelete("credentials/{identifier}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RemoveCredential(
        string identifier,
        [FromQuery, BindRequired] Guid holderId,
        CancellationToken ct)
    {
        if (holderId == Guid.Empty)
        {
            return Problem(
                title: "Missing cleanup holder.",
                detail: "holderId is required for fail-closed cleanup on every gateway replica.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://jeeb.dev/errors/dev-partner-cleanup-holder-required");
        }
        RuntimeCredentialSession removedSession;
        try
        {
            removedSession = await _credentials.RemoveAsync(identifier, holderId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RuntimeCredentialNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log.LogWarning(ex, "partner.auth.dev rejected mismatched cleanup identity.");
            return Problem(
                title: "Partner cleanup identity mismatch.",
                detail: "The identifier and holderId do not name the same runtime credential.",
                statusCode: StatusCodes.Status409Conflict,
                type: "https://jeeb.dev/errors/dev-partner-cleanup-conflict");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "partner.auth.dev cleanup state dependency is unavailable.");
            return CleanupUnavailableProblem();
        }
        if (string.IsNullOrWhiteSpace(removedSession.SessionFamilyId))
            return NoContent();

        try
        {
            await _tokens.RevokeBoundedSessionAsync(
                removedSession.SessionFamilyId,
                RevocationReason.DevCredentialRemoved,
                ct);
            return NoContent();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The credential tombstone already rejects its access JWTs. Keep the holder mapping
            // so this idempotent DELETE can retry durable refresh-family revocation.
            _log.LogWarning(ex, "partner.auth.dev could not revoke a removed credential session.");
            return CleanupUnavailableProblem();
        }
    }

    private ObjectResult CleanupUnavailableProblem() => Problem(
        title: "Partner session cleanup unavailable.",
        detail: "The credential is disabled only when the cleanup tombstone committed; retry cleanup.",
        statusCode: StatusCodes.Status502BadGateway,
        type: "https://jeeb.dev/errors/dev-partner-session-cleanup");
}
