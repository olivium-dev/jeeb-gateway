using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using JeebGateway.Users.SavedLocations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmDeleteResponse = JeebGateway.service.ServiceUserManagement.DeleteUserProfileResponse;

namespace JeebGateway.IntegrationTests.Users;

public sealed class OwnerBackedUsersStoreTests
{
    [Fact]
    public async Task PurgePii_Replay_Treats_Absent_Identity_As_Success_And_Finishes_Owner_Cleanup()
    {
        const string userId = "user-replayed-delete";
        var users = new DeleteThenNotFoundUmClient();
        var locations = new RecordingSavedLocations(userId);
        var bans = new RecordingBanClient();
        var services = new ServiceCollection();
        services.AddSingleton<UmClient>(users);
        services.AddSingleton<ISavedLocationStore>(locations);
        services.AddSingleton<IBanServiceClient>(bans);
        await using var provider = services.BuildServiceProvider();
        var store = new OwnerBackedUsersStore(
            provider.GetRequiredService<IServiceScopeFactory>());

        var first = await store.PurgePiiAsync(userId, CancellationToken.None);
        var replay = await store.PurgePiiAsync(userId, CancellationToken.None);

        first.Should().BeTrue();
        replay.Should().BeTrue(
            "user-management 404 proves the canonical identity was already purged");
        users.DeleteCalls.Should().Be(2);
        locations.ListCalls.Should().Be(2,
            "the remaining owner cleanup must still run after the replayed 404");
        locations.DeleteCalls.Should().ContainSingle().Which.Should().Be("location-1");
        bans.ResetCalls.Should().Equal(userId, userId);
    }

    private sealed class DeleteThenNotFoundUmClient : UmClient
    {
        public DeleteThenNotFoundUmClient()
            : base("http://user-management.test/", new HttpClient())
        {
        }

        public int DeleteCalls { get; private set; }

        public override Task<UmDeleteResponse> DeleteAsync(
            string userId,
            CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (DeleteCalls == 1)
                return Task.FromResult(new UmDeleteResponse { Success = true });

            throw new UmApiException(
                "Not Found",
                StatusCodes.Status404NotFound,
                "{}",
                new Dictionary<string, IEnumerable<string>>(),
                null);
        }
    }

    private sealed class RecordingSavedLocations : ISavedLocationStore
    {
        private readonly List<SavedLocation> _locations;

        public RecordingSavedLocations(string userId)
        {
            _locations =
            [
                new SavedLocation
                {
                    Id = "location-1",
                    UserId = userId,
                    Label = "Home",
                    Latitude = 0,
                    Longitude = 0,
                    CreatedAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
                },
            ];
        }

        public int ListCalls { get; private set; }
        public List<string> DeleteCalls { get; } = [];

        public Task<IReadOnlyList<SavedLocation>> ListAsync(
            string userId,
            CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<SavedLocation>>(_locations.ToArray());
        }

        public Task<bool> DeleteAsync(string userId, string id, CancellationToken ct)
        {
            DeleteCalls.Add(id);
            return Task.FromResult(_locations.RemoveAll(item => item.Id == id) == 1);
        }

        public Task<SavedLocation?> GetAsync(
            string userId,
            string id,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<SavedLocation> CreateAsync(
            string userId,
            CreateSavedLocationRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<SavedLocation?> UpdateAsync(
            string userId,
            string id,
            UpdateSavedLocationRequest request,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingBanClient : IBanServiceClient
    {
        public List<string> ResetCalls { get; } = [];

        public Task<BanResetResult> ForceResetAsync(string userId, CancellationToken ct)
        {
            ResetCalls.Add(userId);
            return Task.FromResult(new BanResetResult { Updated = true });
        }

        public Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<BanStatusItem> ApplyBanAsync(
            string userId,
            string banType,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<BanStatusItem> ApplyTerminalBanAsync(
            string userId,
            string policyKey,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
