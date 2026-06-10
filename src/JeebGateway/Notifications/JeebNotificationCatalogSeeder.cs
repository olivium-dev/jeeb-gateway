using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// JEB-1486 cutover step (2) — keeps the deprecated <c>jeeb.*</c> notification
/// localization ALIVE after the de-leak relocated the catalog out of the shared
/// notification-service.
///
/// The de-leak emptied notification-service's locale catalog
/// (<c>notifications: {}</c>) and removed the <c>jeeb.*</c> branches, so the
/// running service no longer localizes any <c>jeeb.*</c> topic on its own. The
/// gateway now OWNS that taxonomy (<see cref="JeebNotificationCatalog"/>). Without
/// re-registering it into the live service, producers that still emit
/// <c>notification_type = "jeeb.offer_received"</c> would silently regress to the
/// generic raw fallback ("You have a new notification for jeeb.offer_received") —
/// a silent behavioral change GR1 forbids.
///
/// This hosted service re-registers every <see cref="JeebNotificationCatalog.All"/>
/// entry (all 8 <c>jeeb.*</c> keys, EN+AR title/body) into the running
/// notification-service via its GENERIC <c>POST /templates/register</c> endpoint
/// (opaque keys — no Jeeb literal lives in the shared service source). The
/// service persists the registration (idempotent upsert keyed on the opaque key)
/// and resolves it through both <c>POST /render</c> and the webhook localization
/// path, restoring the deprecated <c>jeeb.*</c> alias during the deprecation
/// window.
///
/// Guarantees:
///   * IDEMPOTENT — re-runs on every deploy/restart; the upstream upserts on
///     <c>key</c>, so re-registering simply overwrites with identical copy. No
///     duplicates, no drift.
///   * RESILIENT — seeding runs on a background task so it NEVER blocks or
///     crashes boot. If notification-service is briefly unavailable it retries
///     with exponential backoff; on exhausted retries it logs an error and gives
///     up (the next deploy/restart re-seeds).
///   * Uses the existing outbound pipeline — the named HttpClient
///     <see cref="HttpClientName"/> carries the BFF bearer-forwarding +
///     X-Service-Auth signing handlers (bearer is a no-op at boot since there is
///     no inbound request; the X-Service-Auth caller signature still applies).
/// </summary>
public sealed class JeebNotificationCatalogSeeder : IHostedService
{
    /// <summary>
    /// Named <see cref="HttpClient"/> the seeder dials notification-service with.
    /// Registered in <c>Program.cs</c> against <c>ServiceNotificationClient:BaseUrl</c>
    /// with the standard bearer + X-Service-Auth handler chain.
    /// </summary>
    public const string HttpClientName = "NotificationCatalogSeeder";

    /// <summary>Generic, opaque-key registration endpoint on notification-service.</summary>
    public const string RegisterPath = "templates/register";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Bounded retry so a briefly-unavailable upstream is tolerated without
    // spinning forever (the next deploy/restart re-seeds idempotently anyway).
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JeebNotificationCatalogSeeder> _logger;

    private CancellationTokenSource? _cts;
    private Task? _seedTask;

    public JeebNotificationCatalogSeeder(
        IHttpClientFactory httpClientFactory,
        ILogger<JeebNotificationCatalogSeeder> logger,
        int maxAttempts = 6,
        TimeSpan? baseDelay = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget on a background task: seeding an external service must
        // never block or crash gateway boot (resilience contract). The linked
        // token lets StopAsync cancel an in-flight retry loop on shutdown.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _seedTask = Task.Run(() => RunWithRetryAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_seedTask is not null)
        {
            // Give the in-flight loop a brief, bounded chance to observe the
            // cancellation; never let shutdown hang on it.
            await Task.WhenAny(_seedTask, Task.Delay(Timeout.Infinite, cancellationToken))
                .ConfigureAwait(false);
        }
    }

    private async Task RunWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                if (client.BaseAddress is null)
                {
                    _logger.LogWarning(
                        "JeebNotificationCatalog seeder skipped: '{Client}' has no BaseAddress " +
                        "(ServiceNotificationClient:BaseUrl unset). The deprecated jeeb.* alias " +
                        "will NOT be registered in this environment.",
                        HttpClientName);
                    return;
                }

                var count = await SeedAsync(client, _logger, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "JeebNotificationCatalog seeded: registered {Count} jeeb.* template(s) into " +
                    "notification-service via {Path} (attempt {Attempt}). Deprecated jeeb.* " +
                    "localization alias is live.",
                    count, RegisterPath, attempt);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= _maxAttempts)
                {
                    _logger.LogError(
                        ex,
                        "JeebNotificationCatalog seeding FAILED after {Attempts} attempt(s). The " +
                        "deprecated jeeb.* localization alias may be dark until the next " +
                        "deploy/restart re-runs this idempotent seeder.",
                        _maxAttempts);
                    return;
                }

                // Exponential backoff with a sane cap; the upstream may be mid-deploy.
                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), 30_000));
                _logger.LogWarning(
                    ex,
                    "JeebNotificationCatalog seeding attempt {Attempt}/{Max} failed; retrying in {Delay}s.",
                    attempt, _maxAttempts, delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Posts every <see cref="JeebNotificationCatalog.All"/> entry to the generic
    /// <c>POST /templates/register</c> endpoint and returns the number of keys
    /// registered. Throws on the first non-success response so the caller's retry
    /// loop can re-attempt the whole (idempotent) batch.
    ///
    /// Exposed (internal) for unit testing against a fake message handler.
    /// </summary>
    internal static async Task<int> SeedAsync(
        HttpClient client,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var registered = 0;

        foreach (var entry in JeebNotificationCatalog.All)
        {
            var key = entry.Key;

            // key -> locale -> {title, body}. Opaque to the shared service.
            var translations = new Dictionary<string, Dictionary<string, string>>();
            foreach (var byLocale in entry.Value)
            {
                translations[byLocale.Key] = new Dictionary<string, string>
                {
                    ["title"] = byLocale.Value.Title,
                    ["body"] = byLocale.Value.Body,
                };
            }

            var body = new RegisterTemplateRequest
            {
                Key = key,
                Translations = translations,
                Params = Array.Empty<string>(),
            };

            using var response = await client
                .PostAsJsonAsync(RegisterPath, body, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new HttpRequestException(
                    $"notification-service rejected template registration for '{key}' " +
                    $"with HTTP {status}.");
            }

            registered++;
        }

        logger.LogDebug("Posted {Count} jeeb.* template registrations.", registered);
        return registered;
    }

    /// <summary>
    /// Wire shape of the generic notification-service registration endpoint
    /// (<c>POST /templates/register</c>): an opaque key, a per-locale
    /// <c>{title, body}</c> map, and an advisory placeholder list. Serialized with
    /// web (camelCase) defaults so the JSON is <c>{key, translations, params}</c>.
    /// </summary>
    internal sealed class RegisterTemplateRequest
    {
        public string Key { get; init; } = string.Empty;
        public IReadOnlyDictionary<string, Dictionary<string, string>> Translations { get; init; }
            = new Dictionary<string, Dictionary<string, string>>();
        public IReadOnlyList<string> Params { get; init; } = Array.Empty<string>();
    }
}
