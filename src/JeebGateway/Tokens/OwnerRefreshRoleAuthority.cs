using JeebGateway.Users;
using JeebGateway.Users.Moderation;
using Microsoft.Extensions.DependencyInjection;
using Um = JeebGateway.service.ServiceUserManagement;

namespace JeebGateway.Tokens;

/// <summary>Uncached owner reads used before every runtime refresh mint.</summary>
public enum RefreshRoleAuthorityOutcome { Valid, Invalid, Unavailable }

// Explicit adapter signal for a secondary role resolver whose owner read failed.
// Never carry upstream bodies or credentials across the session boundary.
public sealed class RefreshRoleAuthorityUnavailableException : Exception;

public sealed record RefreshRoleAuthorityResult(RefreshRoleAuthorityOutcome Outcome, TokenRoleContext? Context = null)
{
    public static RefreshRoleAuthorityResult FromContext(TokenRoleContext? context) => context is null
        ? new(RefreshRoleAuthorityOutcome.Invalid) : new(RefreshRoleAuthorityOutcome.Valid, context);
}

public interface IRefreshRoleAuthority
{
    Task<RefreshRoleAuthorityResult> ResolveAsync(string userId, CancellationToken ct);
    Task<bool> ProbeAsync(CancellationToken ct);
}

public sealed class OwnerRefreshRoleAuthority(
    IServiceScopeFactory scopes,
    ILogger<OwnerRefreshRoleAuthority> log) : IRefreshRoleAuthority
{
    public async Task<RefreshRoleAuthorityResult> ResolveAsync(string userId, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var expected) || expected == Guid.Empty) return new(RefreshRoleAuthorityOutcome.Invalid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            // The existing UM roles endpoint is an owner-to-owner read and has no
            // access-bearer requirement. Refresh authentication is the validated
            // refresh record; never forward an expired/attacker-supplied bearer.
            var identity = await scope.ServiceProvider.GetRequiredService<Um.ServiceUserManagementClient>()
                .RolesAsync(userId, timeout.Token);
            var roles = identity.Available_roles?.ToArray();
            var active = identity.Active_role;
            if (!Guid.TryParse(identity.UserId, out var actual) || actual != expected
                || roles is null || roles.Length == 0
                || roles.Any(role => string.IsNullOrWhiteSpace(role) || role != role.Trim())
                || string.IsNullOrWhiteSpace(active)
                || !roles.Contains(active, StringComparer.Ordinal)) return new(RefreshRoleAuthorityOutcome.Invalid);

            var moderation = await UserModerationGate.EvaluateAsync(
                scope.ServiceProvider.GetRequiredService<IUserSuspensionSource>(), userId, log, timeout.Token);
            return moderation.Verdict switch
            {
                ModerationVerdict.Proceed => RefreshRoleAuthorityResult.FromContext(new TokenRoleContext(roles, active)),
                ModerationVerdict.Unavailable => new(RefreshRoleAuthorityOutcome.Unavailable),
                _ => new(RefreshRoleAuthorityOutcome.Invalid)
            };
        }
        catch (Um.ApiException<Um.ProblemDetails> ex) when (ex.StatusCode == 404
            && ex.Result.Type == "https://docs.olivium-dev.com/errors/user-not-found"
            && ex.Result.Status == 404)
        {
            return new(RefreshRoleAuthorityOutcome.Invalid);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // No upstream bodies, identity, bearer or exception metadata in logs.
            log.LogWarning("refresh role authority unavailable; retry without rotating token");
            return new(RefreshRoleAuthorityOutcome.Unavailable);
        }
    }

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            // A syntactically valid non-identity exercises the same owner route
            // and database read without enumerating or creating customer rows.
            await scope.ServiceProvider.GetRequiredService<Um.ServiceUserManagementClient>()
                .RolesAsync(Guid.Empty.ToString(), timeout.Token);
            return false; // An unexpected zero-id account is not a valid probe.
        }
        catch (Um.ApiException<Um.ProblemDetails> ex) when (ex.StatusCode == 404
            && ex.Result.Type == "https://docs.olivium-dev.com/errors/user-not-found"
            && ex.Result.Status == 404)
        {
            var moderation = await UserModerationGate.EvaluateAsync(
                scope.ServiceProvider.GetRequiredService<IUserSuspensionSource>(),
                Guid.Empty.ToString(), log, timeout.Token);
            return moderation.Verdict == ModerationVerdict.Proceed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }
}
