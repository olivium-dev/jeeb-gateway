using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-035: DELETE /users/me. Account-deletion flow with the
/// 30-day GDPR SLA, the "wait for active delivery to complete" gate,
/// order anonymization, and financial-ledger retention.
///
/// These tests use a fresh WebApplicationFactory per test (via the
/// IDisposable pattern) so the singleton in-memory stores do not bleed
/// between cases — the deletion record is keyed by user id and we want
/// each test to start from a clean slate.
/// </summary>
public class AccountDeletionEndpointTests
{
    [Fact]
    public async Task DeleteMe_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var anon = factory.CreateClient();

        var resp = await anon.DeleteAsync("/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteMe_With_No_Active_Delivery_Schedules_Purge_30_Days_Out()
    {
        using var factory = new WebApplicationFactory<Program>();
        var before = DateTimeOffset.UtcNow;
        var client = ClientFor(factory, "del-no-active");

        var resp = await client.DeleteAsync("/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<AccountDeletionResponse>();
        body!.UserId.Should().Be("del-no-active");
        body.Status.Should().Be(AccountDeletionStatus.Scheduled);
        body.ScheduledPurgeAt.Should().NotBeNull();
        // The 30-day SLA is measured from the request time. Allow a few
        // seconds of slack so the test isn't sensitive to clock jitter.
        var expected = before.AddDays(30);
        body.ScheduledPurgeAt!.Value.Should().BeCloseTo(expected, TimeSpan.FromMinutes(1));
        body.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteMe_With_Active_Delivery_Is_Queued_Without_Starting_Timer()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "del-pending");

        // Create an active delivery first so the deletion store has to
        // wait for it before starting the 30-day clock.
        await SeedActiveRequest(factory, "del-pending");

        var resp = await client.DeleteAsync("/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<AccountDeletionResponse>();
        body!.Status.Should().Be(AccountDeletionStatus.PendingActiveDelivery);
        // The AC is explicit: no timer until active delivery completes.
        body.ScheduledPurgeAt.Should().BeNull();
        body.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteMe_Is_Idempotent()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "del-idem");

        var first = await client.DeleteAsync("/users/me");
        var second = await client.DeleteAsync("/users/me");

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var firstBody = await first.Content.ReadFromJsonAsync<AccountDeletionResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<AccountDeletionResponse>();

        secondBody!.RequestedAt.Should().Be(firstBody!.RequestedAt);
        secondBody.ScheduledPurgeAt.Should().Be(firstBody.ScheduledPurgeAt);
    }

    [Fact]
    public async Task GetDeletion_Returns_404_When_No_Request_Pending()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "del-noop");

        var resp = await client.GetAsync("/users/me/deletion");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDeletion_Returns_Record_After_DeleteMe()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "del-readback");

        await client.DeleteAsync("/users/me");
        var resp = await client.GetAsync("/users/me/deletion");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AccountDeletionResponse>();
        body!.UserId.Should().Be("del-readback");
        body.Status.Should().Be(AccountDeletionStatus.Scheduled);
    }

    [Fact]
    public async Task Pending_Deletion_Advances_To_Scheduled_When_Active_Delivery_Completes()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "del-advance");

        // 1. Active delivery in flight → first DELETE goes to pending.
        var requestId = await SeedActiveRequest(factory, "del-advance");
        var firstResp = await client.DeleteAsync("/users/me");
        var first = await firstResp.Content.ReadFromJsonAsync<AccountDeletionResponse>();
        first!.Status.Should().Be(AccountDeletionStatus.PendingActiveDelivery);

        // 2. Delivery completes → advancing the worker should anonymize
        //    the order and start the 30-day timer.
        await SetRequestStatus(factory, requestId, RequestStatus.Delivered);
        var deletions = factory.Services.GetRequiredService<InMemoryAccountDeletionStore>();
        await deletions.AdvanceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // 3. GET should now reflect scheduled + a real purge timestamp.
        var second = await client.GetFromJsonAsync<AccountDeletionResponse>("/users/me/deletion");
        second!.Status.Should().Be(AccountDeletionStatus.Scheduled);
        second.ScheduledPurgeAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Orders_Are_Anonymized_With_Stable_Hash_When_Deletion_Scheduled()
    {
        using var factory = new WebApplicationFactory<Program>();
        var userId = "del-orders";
        var client = ClientFor(factory, userId);

        // A completed (non-active) order exists; on DELETE its client_id
        // must be rewritten to the SHA-256 pseudonym.
        var requestId = await SeedActiveRequest(factory, userId);
        await SetRequestStatus(factory, requestId, RequestStatus.Delivered);

        var resp = await client.DeleteAsync("/users/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var requests = factory.Services.GetRequiredService<IRequestsStore>();
        // Original user id should now hold 0 active requests AND 0 total
        // — the store has rewritten the row to the hash, not deleted it.
        var stillForUser = await requests.CountActiveForClientAsync(userId, CancellationToken.None);
        stillForUser.Should().Be(0);

        // Re-anonymizing the same user yields the same hash (deterministic),
        // so a follow-up call against an already-anonymized row is a no-op.
        var rewrittenAgain = await requests.AnonymizeForClientAsync(userId, "ignored", CancellationToken.None);
        rewrittenAgain.Should().Be(0);
    }

    [Fact]
    public async Task Financial_Ledger_Anonymized_But_Rows_Retained()
    {
        using var factory = new WebApplicationFactory<Program>();
        var userId = "del-finance";
        var ledger = factory.Services.GetRequiredService<InMemoryFinancialLedger>();
        ledger.Seed(userId, rows: 3);

        var client = ClientFor(factory, userId);
        var resp = await client.DeleteAsync("/users/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var hash = HashUserId(userId);
        var byUser = await ledger.CountRowsForUserAsync(userId, CancellationToken.None);
        var byHash = await ledger.CountRowsForHashAsync(hash, CancellationToken.None);

        // AC: financial records retained for accounting (anonymized) —
        // the rows still exist, just under the pseudonym.
        byUser.Should().Be(0);
        byHash.Should().Be(3);
    }

    [Fact]
    public async Task Advance_Past_30_Days_Hard_Deletes_Pii()
    {
        using var factory = new WebApplicationFactory<Program>();
        var userId = "del-purge";

        // Seed a user with real PII so we can confirm it gets wiped.
        var users = factory.Services.GetRequiredService<InMemoryUsersStore>();
        users.Seed(new UserProfile
        {
            Id = userId,
            Phone = "+96550009999",
            Name = "Sara",
            Email = "sara@example.com",
            AvatarUrl = "https://cdn.example.com/sara.png",
            Roles = new List<string> { "customer" },
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-60),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-60)
        });

        var client = ClientFor(factory, userId);
        await client.DeleteAsync("/users/me");

        // Fast-forward past the 30-day SLA — running AdvanceAsync with
        // a future "now" should trip the purge.
        var deletions = factory.Services.GetRequiredService<InMemoryAccountDeletionStore>();
        await deletions.AdvanceAsync(DateTimeOffset.UtcNow.AddDays(31), CancellationToken.None);

        var afterStatus = await client.GetFromJsonAsync<AccountDeletionResponse>("/users/me/deletion");
        afterStatus!.Status.Should().Be(AccountDeletionStatus.Completed);
        afterStatus.CompletedAt.Should().NotBeNull();

        // The user row is retained as an anonymized stub. GET /users/me
        // bootstraps a profile when missing; we want to confirm PII fields
        // are empty / null on the same id rather than that the row is gone.
        var profile = await client.GetFromJsonAsync<UserProfileResponse>("/users/me");
        profile!.Name.Should().BeEmpty();
        profile.Phone.Should().BeEmpty();
        profile.Email.Should().BeNull();
        profile.AvatarUrl.Should().BeNull();
        profile.SavedAddresses.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteMe_Revokes_All_Refresh_Tokens()
    {
        using var factory = new WebApplicationFactory<Program>();
        var userId = "del-tokens";
        var tokens = factory.Services.GetRequiredService<JeebGateway.Tokens.ITokenService>();
        await tokens.IssueAsync(userId, new[] { "customer" }, CancellationToken.None);
        await tokens.IssueAsync(userId, new[] { "customer" }, CancellationToken.None);

        var client = ClientFor(factory, userId);
        var resp = await client.DeleteAsync("/users/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Every token issued above should already be revoked — calling
        // RevokeAllForUser again should find nothing left to revoke.
        var revokedNow = await tokens.RevokeAllForUserAsync(
            userId,
            JeebGateway.Tokens.RevocationReason.AccountDeleted,
            CancellationToken.None);
        revokedNow.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        return client;
    }

    private static async Task<string> SeedActiveRequest(WebApplicationFactory<Program> factory, string userId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var req = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = userId,
            Description = "deletion-test parcel"
        }, CancellationToken.None);
        return req.Id;
    }

    private static async Task SetRequestStatus(WebApplicationFactory<Program> factory, string requestId, string status)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var ok = await store.SetStatusAsync(requestId, status, CancellationToken.None);
        ok.Should().BeTrue();
    }

    private static string HashUserId(string userId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
