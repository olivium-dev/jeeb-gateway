using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;
using FeedbackClient = JeebGateway.service.ServiceFeedback.ServiceFeedbackClient;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmProfile = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.Users;

/// <summary>
/// Stateless CMS/admin projection composed at request time from the services that
/// own each field: user-management for identity and roles, ban-service for account
/// suspension, and feedback-service for rating aggregates. Implementations must not
/// persist or cache the composed rows in the gateway.
/// </summary>
public interface IAdminUserProjection
{
    Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct);
    Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct);
    Task<UserProfile?> SuspendAsync(
        string userId, string reason, string adminId, CancellationToken ct);
    Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct);
}

public sealed class AdminUserBanOptions
{
    public const string SectionName = "Services:Ban";

    /// <summary>
    /// ban-service policy whose configured terminal stage represents an explicit
    /// administrator suspension. This is an opaque owner-side key.
    /// </summary>
    public string AdminPolicyKey { get; init; } = "red";
}

/// <inheritdoc cref="IAdminUserProjection"/>
public sealed class OwnerComposedAdminUsers : IAdminUserProjection
{
    private const int UpstreamPageSize = 200;
    private const int MaxUpstreamPages = 100;

    private readonly UmClient _users;
    private readonly FeedbackClient _feedback;
    private readonly IBanServiceClient _ban;
    private readonly string _adminPolicyKey;

    public OwnerComposedAdminUsers(
        UmClient users,
        FeedbackClient feedback,
        IBanServiceClient ban,
        IOptions<AdminUserBanOptions> options)
    {
        _users = users;
        _feedback = feedback;
        _ban = ban;
        _adminPolicyKey = options.Value.AdminPolicyKey?.Trim() ?? string.Empty;
        if (_adminPolicyKey.Length == 0)
        {
            throw new InvalidOperationException(
                $"{AdminUserBanOptions.SectionName}:AdminPolicyKey must be configured.");
        }
    }

    public async Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
    {
        var identities = await ListAllIdentitiesAsync(ct);
        var filtered = identities
            .Where(u => Contains(u.Username, query.Name))
            .Where(u => Contains(u.Email, query.Email))
            // UM's public roster/profile contract intentionally carries no phone.
            // A phone filter therefore has no matches instead of consulting a
            // gateway projection table that would become a second identity owner.
            .Where(_ => string.IsNullOrWhiteSpace(query.Phone))
            .OrderByDescending(u => ParseCreated(u.CreatedDate))
            .ThenBy(u => u.UserId, StringComparer.Ordinal)
            .ToList();

        var page = Math.Max(query.Page, 1);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var pageRows = filtered.Skip((page - 1) * size).Take(size).ToList();
        var composed = await Task.WhenAll(pageRows.Select(row => ComposeAsync(row, ct)));

        return new UserSearchResult
        {
            Items = composed,
            Total = filtered.Count,
        };
    }

    public async Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
    {
        var identity = await GetIdentityAsync(userId, ct);
        return identity is null ? null : await ComposeAsync(identity, ct);
    }

    public async Task<UserProfile?> SuspendAsync(
        string userId, string reason, string adminId, CancellationToken ct)
    {
        var identity = await GetIdentityAsync(userId, ct);
        if (identity is null)
        {
            return null;
        }

        var status = await _ban.ApplyTerminalBanAsync(userId, _adminPolicyKey, ct);
        if (!status.IsCurrentlyBanned
            || !string.Equals(status.Status, "BAN", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ban-service did not confirm the terminal BAN stage for the admin suspension.");
        }

        var profile = MapIdentity(identity);
        ApplyBan(profile, status);
        // The free-form CMS reason and acting administrator remain in the gateway's
        // immutable admin audit record. ban-service owns only the configured policy
        // message/stage, so these request-scoped fields are not persisted as a cache.
        profile.SuspensionReason = reason;
        profile.SuspendedBy = adminId;
        return profile;
    }

    public async Task<UserProfile?> UnsuspendAsync(
        string userId, string adminId, CancellationToken ct)
    {
        var identity = await GetIdentityAsync(userId, ct);
        if (identity is null)
        {
            return null;
        }

        await _ban.ForceResetAsync(userId, ct);
        var profile = MapIdentity(identity);
        profile.IsSuspended = false;
        profile.SuspensionReason = null;
        profile.SuspendedAt = null;
        profile.SuspendedBy = adminId;
        return profile;
    }

    private async Task<IReadOnlyList<UmProfile>> ListAllIdentitiesAsync(CancellationToken ct)
    {
        var rows = new List<UmProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skip = 0;

        for (var page = 0; page < MaxUpstreamPages; page++)
        {
            var batch = await _users.AllAsync(skip, UpstreamPageSize, null, ct);
            var returned = batch.Users?.ToList() ?? new List<UmProfile>();
            foreach (var row in returned)
            {
                if (!string.IsNullOrWhiteSpace(row.UserId) && seen.Add(row.UserId))
                {
                    rows.Add(row);
                }
            }

            if (!batch.HasMore || returned.Count == 0)
            {
                break;
            }

            // UM may cap its page below our requested size, so advance by what it
            // actually returned rather than skipping valid users.
            skip += returned.Count;
        }

        return rows;
    }

    private async Task<UmProfile?> GetIdentityAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        try
        {
            return await _users.ProfileAsync(userId, ct);
        }
        catch (UmApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    private async Task<UserProfile> ComposeAsync(UmProfile identity, CancellationToken ct)
    {
        var profile = MapIdentity(identity);
        var banTask = _ban.GetStatusAsync(profile.Id, ct);

        Task<JeebGateway.service.ServiceFeedback.RateeReviewsResponse?> ratingTask =
            Guid.TryParse(profile.Id, out var rateeId)
                ? ReadRatingAsync(rateeId, ct)
                : Task.FromResult<JeebGateway.service.ServiceFeedback.RateeReviewsResponse?>(null);

        await Task.WhenAll(banTask, ratingTask);
        var bans = await banTask;
        var active = bans.BanStatuses
            .Where(status => status.IsCurrentlyBanned)
            .OrderByDescending(status => status.LastUpdated)
            .FirstOrDefault();
        if (active is not null)
        {
            ApplyBan(profile, active);
        }

        var rating = await ratingTask;
        if (rating is not null && rating.TotalReviewCount > 0)
        {
            profile.Rating = Convert.ToDecimal(rating.AverageRating);
            profile.RatingCount = rating.TotalReviewCount;
        }

        return profile;
    }

    private async Task<JeebGateway.service.ServiceFeedback.RateeReviewsResponse?> ReadRatingAsync(
        Guid rateeId, CancellationToken ct)
    {
        try
        {
            // Only aggregate fields are needed; requesting one review avoids pulling
            // an unbounded review body into the CMS roster composition.
            return await _feedback.RatingsByRateeAsync(rateeId, 1, 0, ct);
        }
        catch (JeebGateway.service.ServiceFeedback.ApiException ex)
            when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    private static UserProfile MapIdentity(UmProfile row)
    {
        var created = ParseCreated(row.CreatedDate);
        return new UserProfile
        {
            Id = row.UserId ?? string.Empty,
            Phone = string.Empty,
            Email = row.Email,
            Name = row.Username ?? string.Empty,
            AvatarUrl = row.ProfilePic,
            Language = "en",
            Roles = row.Available_roles?.Where(role => !string.IsNullOrWhiteSpace(role)).ToList()
                ?? new List<string>(),
            ActiveRole = string.IsNullOrWhiteSpace(row.Active_role) ? Roles.Client : row.Active_role!,
            CreatedAt = created,
            UpdatedAt = created,
        };
    }

    private static void ApplyBan(UserProfile profile, BanStatusItem status)
    {
        profile.IsSuspended = status.IsCurrentlyBanned;
        profile.SuspensionReason = Moderation.ModerationReason.ForOperator(status.Message);
        profile.SuspendedAt = status.LastUpdated;
    }

    private static bool Contains(string? value, string? needle)
        => string.IsNullOrWhiteSpace(needle)
           || (value?.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static DateTimeOffset ParseCreated(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
}
