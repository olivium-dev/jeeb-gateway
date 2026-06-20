using JeebGateway.Users.SavedLocations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.Extensions;

/// <summary>
/// WS-02 — DI wiring for the Saved Locations BFF (ACCT-04 / REQ-02). One call
/// added to Program.cs keeps the hot file to a single line and isolates this
/// net-new feature's registration here.
///
/// <para>Net-new and gateway-thin: the only store today is the in-memory one,
/// registered as a singleton (state must outlive the request scope, mirroring
/// <c>InMemoryNotificationPreferencesStore</c>). When a geolocation-service /
/// user-management saved-locations upstream ships, add a remote NSwag-backed
/// store here behind a feature flag (default OFF) with a 503 kill-switch — never
/// a hand-rolled HttpClient.</para>
///
/// Idempotent via <c>TryAddSingleton</c> so a future remote registration that
/// runs first continues to win.
/// </summary>
public static class SavedLocationsServiceCollectionExtensions
{
    public static IServiceCollection AddSavedLocations(this IServiceCollection services)
    {
        services.TryAddSingleton<ISavedLocationStore, InMemorySavedLocationStore>();
        return services;
    }
}
