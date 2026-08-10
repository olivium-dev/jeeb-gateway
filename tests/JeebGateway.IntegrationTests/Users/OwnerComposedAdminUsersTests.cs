using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.Extensions.Options;
using Xunit;
using FeedbackClient = JeebGateway.service.ServiceFeedback.ServiceFeedbackClient;
using RateeReviews = JeebGateway.service.ServiceFeedback.RateeReviewsResponse;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmList = JeebGateway.service.ServiceUserManagement.GetAllUsersResponse;
using UmProfile = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.IntegrationTests.Users;

public sealed class OwnerComposedAdminUsersTests
{
    private static readonly Guid AliceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Search_Composes_Um_Ban_And_Feedback_Without_Local_Store()
    {
        var alice = Profile(AliceId.ToString(), "Alice", "alice@example.test");
        var bob = Profile("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Bob", "bob@example.test");
        var um = new StubUmClient(new[] { alice, bob });
        var feedback = new StubFeedbackClient(new Dictionary<Guid, RateeReviews>
        {
            [AliceId] = new() { RateeId = AliceId, TotalReviewCount = 3, AverageRating = 4.5 },
        });
        var ban = new StubBanClient
        {
            Statuses =
            {
                [AliceId.ToString()] = new BanStatusesResult
                {
                    UserId = AliceId.ToString(),
                    BanStatuses = new[]
                    {
                        new BanStatusItem
                        {
                            UserId = AliceId.ToString(), BanType = "red", CurrentStage = 2,
                            Status = "BAN", Message = "suspended", IsCurrentlyBanned = true,
                            LastUpdated = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
                        },
                    },
                },
            },
        };
        var projection = NewProjection(um, feedback, ban);

        var result = await projection.SearchAsync(new UserSearchQuery
        {
            Name = "ali", Page = 1, PageSize = 20,
        }, CancellationToken.None);

        result.Total.Should().Be(1);
        var row = result.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(AliceId.ToString());
        row.Rating.Should().Be(4.5m);
        row.RatingCount.Should().Be(3);
        row.IsSuspended.Should().BeTrue();
        row.SuspensionReason.Should().Be("suspended");
        um.AllCalls.Should().Be(1);
        ban.StatusCalls.Should().ContainSingle().Which.Should().Be(AliceId.ToString());
    }

    [Fact]
    public async Task Suspend_Uses_Terminal_Policy_And_Unsuspend_Uses_Force_Reset()
    {
        var identity = Profile(AliceId.ToString(), "Alice", "alice@example.test");
        var um = new StubUmClient(new[] { identity });
        var ban = new StubBanClient
        {
            TerminalResult = new BanStatusItem
            {
                UserId = AliceId.ToString(), BanType = "cms-admin", CurrentStage = 4,
                Status = "BAN", Message = "terminal", IsCurrentlyBanned = true,
                LastUpdated = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            },
        };
        var projection = NewProjection(um, new StubFeedbackClient(), ban, "cms-admin");

        var suspended = await projection.SuspendAsync(
            AliceId.ToString(), "manual review", "ops-admin", CancellationToken.None);
        var unsuspended = await projection.UnsuspendAsync(
            AliceId.ToString(), "ops-admin", CancellationToken.None);

        suspended!.IsSuspended.Should().BeTrue();
        suspended.SuspensionReason.Should().Be("manual review");
        suspended.SuspendedBy.Should().Be("ops-admin");
        unsuspended!.IsSuspended.Should().BeFalse();
        ban.TerminalCalls.Should().ContainSingle()
            .Which.Should().Be((AliceId.ToString(), "cms-admin"));
        ban.ResetCalls.Should().ContainSingle().Which.Should().Be(AliceId.ToString());
    }

    private static OwnerComposedAdminUsers NewProjection(
        UmClient um, FeedbackClient feedback, IBanServiceClient ban, string policy = "red")
        => new(um, feedback, ban,
            Options.Create(new AdminUserBanOptions { AdminPolicyKey = policy }));

    private static UmProfile Profile(string id, string name, string email) => new()
    {
        UserId = id,
        Username = name,
        Email = email,
        CreatedDate = "2026-08-10T10:00:00Z",
        Available_roles = new[] { Roles.Client },
        Active_role = Roles.Client,
    };

    private sealed class StubUmClient : UmClient
    {
        private readonly IReadOnlyList<UmProfile> _profiles;

        public StubUmClient(IReadOnlyList<UmProfile> profiles)
            : base("http://um.test/", new HttpClient()) => _profiles = profiles;

        public int AllCalls { get; private set; }

        public override Task<UmList> AllAsync(
            int? skip, int? limit, bool? onActive, CancellationToken cancellationToken)
        {
            AllCalls++;
            var rows = _profiles.Skip(skip ?? 0).Take(limit ?? 200).ToList();
            return Task.FromResult(new UmList
            {
                Users = rows,
                TotalCount = _profiles.Count,
                Skip = skip ?? 0,
                Limit = limit ?? 200,
                HasMore = (skip ?? 0) + rows.Count < _profiles.Count,
            });
        }

        public override Task<UmProfile> ProfileAsync(
            string userId, CancellationToken cancellationToken)
            => Task.FromResult(_profiles.Single(row => row.UserId == userId));
    }

    private sealed class StubFeedbackClient : FeedbackClient
    {
        private readonly IReadOnlyDictionary<Guid, RateeReviews> _ratings;

        public StubFeedbackClient(IReadOnlyDictionary<Guid, RateeReviews>? ratings = null)
            : base("http://feedback.test/", new HttpClient())
            => _ratings = ratings ?? new Dictionary<Guid, RateeReviews>();

        public override Task<RateeReviews> RatingsByRateeAsync(
            Guid rateeId, int length, int offset, CancellationToken cancellationToken)
            => Task.FromResult(_ratings.TryGetValue(rateeId, out var value)
                ? value
                : new RateeReviews
                {
                    RateeId = rateeId, TotalReviewCount = 0, AverageRating = 0,
                });
    }

    private sealed class StubBanClient : IBanServiceClient
    {
        public Dictionary<string, BanStatusesResult> Statuses { get; } = new();
        public List<string> StatusCalls { get; } = new();
        public List<(string UserId, string Policy)> TerminalCalls { get; } = new();
        public List<string> ResetCalls { get; } = new();
        public BanStatusItem TerminalResult { get; init; } = new()
        {
            Status = "BAN", IsCurrentlyBanned = true,
        };

        public Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct)
        {
            StatusCalls.Add(userId);
            return Task.FromResult(Statuses.TryGetValue(userId, out var value)
                ? value
                : new BanStatusesResult { UserId = userId });
        }

        public Task<BanStatusItem> ApplyBanAsync(
            string userId, string banType, CancellationToken ct)
            => throw new InvalidOperationException("progressive apply must not be used by CMS admin");

        public Task<BanStatusItem> ApplyTerminalBanAsync(
            string userId, string policyKey, CancellationToken ct)
        {
            TerminalCalls.Add((userId, policyKey));
            return Task.FromResult(TerminalResult);
        }

        public Task<BanResetResult> ForceResetAsync(string userId, CancellationToken ct)
        {
            ResetCalls.Add(userId);
            return Task.FromResult(new BanResetResult { Updated = true });
        }
    }
}
