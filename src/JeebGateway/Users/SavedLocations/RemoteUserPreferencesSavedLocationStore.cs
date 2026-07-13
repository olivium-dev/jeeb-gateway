using System.Text.Json;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Users.SavedLocations;

/// <summary>
/// Production <see cref="ISavedLocationStore"/> backed by the generic
/// remote-user-preferences service (Rust, :10067) — the owning service for
/// user-scoped state per GR-2 / GR-3 (D1 matrix row 5, JEBV4-165 / JEBV4-194 D5).
///
/// <para>This REPLACES the gateway-owned <c>PostgresSavedLocationStore</c>
/// (<c>saved_locations</c> table, migration 0016), removing the gateway's direct
/// Postgres seam for saved locations and moving the durable state to its upstream
/// owner. It is the exact sibling of
/// <see cref="JeebGateway.NotificationPreferences.RemoteUserPreferencesNotificationPreferencesStore"/>:
/// the whole per-user collection is stored as one opaque JSON blob under the
/// namespaced key <c>jeeb.saved_locations</c>, so the shared service stays
/// Jeeb-agnostic (GR-2 / JEB-1498) and learns nothing about saved-location shape.</para>
///
/// <para>The remote store exposes only whole-blob get/set, so every mutation
/// (create/update/delete) is a read-modify-write of the full collection. The
/// REQ-02 "exactly one default per user" invariant is applied in-memory on the
/// read-modify-write path, identical to <see cref="InMemorySavedLocationStore"/>.
/// Reads degrade to an empty list on an upstream fault (the list surface tolerates
/// empty and recovers on the next refresh); writes ABORT on an unanswered pre-read
/// so a transient upstream blip can never clobber the user's stored collection with
/// a defaults-only overwrite.</para>
/// </summary>
/// <remarks>
/// Registered as a singleton (uniform with the other stores), but the underlying
/// <see cref="ServiceRemoteUserPreferencesClient"/> is scoped (it wraps a pooled
/// <c>IHttpClientFactory</c> client). To avoid a captive dependency the scoped
/// client is resolved lazily inside a per-call <see cref="IServiceScope"/> via
/// <see cref="IServiceScopeFactory"/> rather than constructor-injected — the same
/// lifetime discipline as the notification-preferences store.
/// </remarks>
public sealed class RemoteUserPreferencesSavedLocationStore : ISavedLocationStore
{
    private const string BlobKey = "jeeb.saved_locations";

    // Saved locations are a low-frequency, user-driven surface. Bound the whole
    // store operation so a slow/erroring upstream fails fast (well under the
    // mobile client's write timeout) instead of spinning the named client's
    // retry pipeline (3 x 10s). Reads fail open to empty; writes fail fast.
    private static readonly TimeSpan UpstreamReadBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan UpstreamWriteBudget = TimeSpan.FromMilliseconds(2000);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RemoteUserPreferencesSavedLocationStore> _logger;

    public RemoteUserPreferencesSavedLocationStore(
        IServiceScopeFactory scopeFactory,
        ILogger<RemoteUserPreferencesSavedLocationStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SavedLocation>> ListAsync(string userId, CancellationToken ct)
    {
        var (_, items) = await ReadInternalAsync(userId, ct);
        // Same ordering as InMemorySavedLocationStore: default first, then oldest-first.
        return items
            .OrderByDescending(l => l.IsDefault)
            .ThenBy(l => l.CreatedAt)
            .Select(e => ToModel(userId, e))
            .ToList();
    }

    public async Task<SavedLocation?> GetAsync(string userId, string id, CancellationToken ct)
    {
        var (_, items) = await ReadInternalAsync(userId, ct);
        var found = items.FirstOrDefault(e => e.Id == id);
        return found is null ? null : ToModel(userId, found);
    }

    public async Task<SavedLocation> CreateAsync(string userId, CreateSavedLocationRequest request, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpstreamWriteBudget);

        var (answered, items) = await ReadInternalAsync(userId, budget.Token);
        if (!answered)
            throw new TimeoutException(
                "remote-user-preferences did not respond within the read budget; aborting the " +
                "saved-location create to avoid overwriting the stored collection.");

        var now = DateTimeOffset.UtcNow;
        // REQ-02: first saved location is the implicit default if the caller did not ask.
        var makeDefault = request.IsDefault || items.Count == 0;
        var created = new SavedLocationEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = request.Label,
            Address = request.Address,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            IsDefault = makeDefault,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (makeDefault) ClearDefaults(items);
        items.Add(created);

        await WriteAsync(userId, items, budget.Token, ct);
        return ToModel(userId, created);
    }

    public async Task<SavedLocation?> UpdateAsync(string userId, string id, UpdateSavedLocationRequest request, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpstreamWriteBudget);

        var (answered, items) = await ReadInternalAsync(userId, budget.Token);
        if (!answered)
            throw new TimeoutException(
                "remote-user-preferences did not respond within the read budget; aborting the " +
                "saved-location update to avoid overwriting the stored collection.");

        var existing = items.FirstOrDefault(e => e.Id == id);
        if (existing is null) return null;

        if (request.Label is { } label) existing.Label = label;
        if (request.Address is { } address) existing.Address = address;
        if (request.Latitude is { } lat) existing.Latitude = lat;
        if (request.Longitude is { } lng) existing.Longitude = lng;

        if (request.IsDefault is true)
        {
            ClearDefaults(items);
            existing.IsDefault = true;
        }
        else if (request.IsDefault is false)
        {
            existing.IsDefault = false;
        }

        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await WriteAsync(userId, items, budget.Token, ct);
        return ToModel(userId, existing);
    }

    public async Task<bool> DeleteAsync(string userId, string id, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpstreamWriteBudget);

        var (answered, items) = await ReadInternalAsync(userId, budget.Token);
        if (!answered)
            throw new TimeoutException(
                "remote-user-preferences did not respond within the read budget; aborting the " +
                "saved-location delete to avoid overwriting the stored collection.");

        var removed = items.FirstOrDefault(e => e.Id == id);
        if (removed is null) return false;
        items.Remove(removed);

        // REQ-02: if we removed the default, promote the oldest remaining so the
        // user always has a "my location" while any saved location exists.
        if (removed.IsDefault)
        {
            var next = items.OrderBy(l => l.CreatedAt).FirstOrDefault();
            if (next is not null)
            {
                next.IsDefault = true;
                next.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await WriteAsync(userId, items, budget.Token, ct);
        return true;
    }

    /// <summary>
    /// Reads the saved-location collection blob under a bounded deadline. Returns
    /// <c>answered=true</c> when the upstream gave a definitive answer (a blob, an
    /// empty/absent blob, or a 404 "nothing stored yet") and <c>answered=false</c>
    /// when the call errored or exceeded <see cref="UpstreamReadBudget"/> — in which
    /// case callers still get an empty (mutable) list but know it is NOT
    /// authoritative. A genuine caller cancellation is propagated.
    /// </summary>
    private async Task<(bool answered, List<SavedLocationEntry> items)> ReadInternalAsync(string userId, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpstreamReadBudget);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ServiceRemoteUserPreferencesClient>();
            var pref = await client.Data_GetSinglePreferenceAsync(userId, BlobKey, budget.Token);
            if (pref?.Value is { } json)
            {
                var blob = JsonSerializer.Deserialize<SavedLocationsBlob>(json, SerializerOptions);
                if (blob?.Items is { } stored)
                    return (true, stored);
            }
            // 200 with an empty/unparseable body: treat as "nothing stored yet".
            return (true, new List<SavedLocationEntry>());
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // First access: no saved locations stored yet — a definitive answer.
            return (true, new List<SavedLocationEntry>());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER aborted (not our budget) — propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "saved-locations read for {UserId} failed or exceeded {BudgetMs}ms; degrading to empty",
                userId, UpstreamReadBudget.TotalMilliseconds);
            return (false, new List<SavedLocationEntry>());
        }
    }

    /// <summary>
    /// Persists the whole collection blob (update, falling back to set on a 404
    /// "no key yet"). Bounded by the caller's write budget; a budget expiry that is
    /// NOT a genuine caller cancellation surfaces as a fast <see cref="TimeoutException"/>.
    /// </summary>
    private async Task WriteAsync(string userId, List<SavedLocationEntry> items, CancellationToken budgetToken, CancellationToken callerCt)
    {
        var json = JsonSerializer.Serialize(new SavedLocationsBlob { Items = items }, SerializerOptions);
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ServiceRemoteUserPreferencesClient>();
        try
        {
            try
            {
                await client.Data_UpdatePreferenceAsync(userId, BlobKey, new PreferenceValue { Value = json }, budgetToken);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                await client.Data_SetSinglePreferenceAsync(userId, BlobKey, new PreferenceValue { Value = json }, budgetToken);
            }
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            throw new TimeoutException(
                "remote-user-preferences did not accept the saved-location write within the budget.");
        }
    }

    private static void ClearDefaults(List<SavedLocationEntry> items)
    {
        foreach (var loc in items)
            loc.IsDefault = false;
    }

    private static SavedLocation ToModel(string userId, SavedLocationEntry e) => new()
    {
        Id = e.Id,
        UserId = userId,
        Label = e.Label,
        Address = e.Address,
        Latitude = e.Latitude,
        Longitude = e.Longitude,
        IsDefault = e.IsDefault,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };

    /// <summary>The opaque per-user blob persisted under <see cref="BlobKey"/>.</summary>
    private sealed class SavedLocationsBlob
    {
        public List<SavedLocationEntry> Items { get; set; } = new();
    }

    /// <summary>
    /// Serialized shape of one saved location inside the blob. Mutable (the
    /// read-modify-write path edits entries in place before re-serializing) and
    /// user-id-free — the userId is the blob's owning key, so it is never stored
    /// redundantly inside the blob.
    /// </summary>
    private sealed class SavedLocationEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? Address { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public bool IsDefault { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
