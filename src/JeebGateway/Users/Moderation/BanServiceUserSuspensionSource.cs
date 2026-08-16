using JeebGateway.Services.Clients;

namespace JeebGateway.Users.Moderation;

/// <summary>
/// Reads suspension from ban-service — the service the product's own admin suspend action writes.
///
/// <para>This is the same read <see cref="RequireActiveUserFilter"/> already performs on every
/// <c>[RequireActiveUser]</c> endpoint, and the same derivation
/// <see cref="OwnerBackedUsersStore.GetForModerationAsync"/> spells out: any row reporting
/// <see cref="BanStatusItem.IsCurrentlyBanned"/> suspends the account, and the most recently
/// updated such row supplies the message. Login now agrees with the rest of the product instead of
/// consulting a store of its own.</para>
///
/// <para>Deliberately no try/catch. An unreachable ban-service must reach
/// <see cref="UserModerationGate"/> as a fault so login fails CLOSED with 503; a caught exception
/// here would mint sessions for suspended accounts during exactly the outage an attacker would
/// wait for.</para>
/// </summary>
public sealed class BanServiceUserSuspensionSource : IUserSuspensionSource
{
    private readonly IBanServiceClient _ban;

    public BanServiceUserSuspensionSource(IBanServiceClient ban)
        => _ban = ban ?? throw new ArgumentNullException(nameof(ban));

    public async Task<UserSuspension> ReadAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return UserSuspension.None;

        var statuses = await _ban.GetStatusAsync(userId, ct).ConfigureAwait(false);

        var active = statuses.BanStatuses
            .Where(status => status.IsCurrentlyBanned)
            .OrderByDescending(status => status.LastUpdated)
            .FirstOrDefault();

        // D16: ban-service's message is the operator's CONFIGURED string, which in the shipped
        // banning-rule.json is an i18n template. Split it here, at the boundary, so no caller
        // can accidentally render `Label{{...}}` as prose.
        return active is null
            ? UserSuspension.None
            : new UserSuspension(
                true,
                ModerationReason.Humanize(active.Message),
                ModerationReason.CodeOf(active.Message));
    }
}
