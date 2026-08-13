using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.NotificationPreferences;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using JeebGateway.Users.SavedLocations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// W0-09F — the RemoteUserPreferences-backed READ paths must not fail OPEN.
///
/// <para>Both remote stores already distinguish "the upstream answered, and the answer is
/// nothing stored yet" (<c>answered:true</c>) from "the upstream could not answer"
/// (<c>answered:false</c>), but the controllers discarded the flag and served the
/// placeholder — an empty saved-location list / the six default notification toggles —
/// as an authoritative 200. A user whose state exists upstream watched it silently vanish.</para>
///
/// <para>These tests pin the O10 precedent (wallet ledger: an unanswerable read is a 502,
/// not an empty 200): <c>answered:false</c> ⇒ 502; <c>answered:true</c> + empty ⇒ 200 with
/// the empty/default body, so a brand-new user's first screen is NEVER an error.</para>
/// </summary>
public class UserPreferencesUnansweredReadFailClosedTests
{
    private const string SavedLocationsRoute = "/api/users/me/saved-locations";
    private const string NotificationPrefsRoute = "/api/users/me/notification-preferences";

    // ---- answered:false (dead upstream) => 502, never an authoritative empty 200 ----

    [Fact]
    public async Task SavedLocations_List_UnansweredUpstream_Returns502_NotEmpty200()
    {
        var stub = new CountingHandler(_ => throw new HttpRequestException(
            "No route to host (simulated unreachable remote-user-preferences)"));
        using var factory = NewFactory(stub);
        var client = ClientFor(factory, "w09f-sl-dead");

        var resp = await client.GetAsync(SavedLocationsRoute);
        var body = await resp.Content.ReadAsStringAsync();

        stub.Calls.Should().BeGreaterThan(0, "the remote store must actually be the one wired in — otherwise this probe proves nothing");
        ((int)resp.StatusCode).Should().Be(502,
            "a read the upstream could not answer is retryable (O10: 502, not an empty 200)");
        body.Should().NotContain("\"items\":[]", "the fail-open empty list must no longer be served as the answer");
    }

    [Fact]
    public async Task NotificationPreferences_Get_UnansweredUpstream_Returns502_NotDefaults200()
    {
        var stub = new CountingHandler(_ => throw new HttpRequestException(
            "No route to host (simulated unreachable remote-user-preferences)"));
        using var factory = NewFactory(stub);
        var client = ClientFor(factory, "w09f-np-dead");

        var resp = await client.GetAsync(NotificationPrefsRoute);

        stub.Calls.Should().BeGreaterThan(0, "the remote store must actually be the one wired in");
        ((int)resp.StatusCode).Should().Be(502,
            "serving the six defaults as a 200 silently un-mutes categories the user muted");
    }

    // ---- answered:true + empty (first-ever call) => 200, MUST stay 200 ----

    [Fact]
    public async Task SavedLocations_List_UpstreamAnswers404_NothingStoredYet_Returns200_Empty()
    {
        var stub = new CountingHandler(_ => NotFound());
        using var factory = NewFactory(stub);
        var client = ClientFor(factory, "w09f-sl-firstcall");

        var resp = await client.GetAsync(SavedLocationsRoute);
        var body = await resp.Content.ReadAsStringAsync();

        stub.Calls.Should().BeGreaterThan(0, "the remote store must actually be the one wired in");
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a first-ever call is a DEFINITIVE 'nothing stored yet' — turning it into an error would break every new user's first screen");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("defaultId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task NotificationPreferences_Get_UpstreamAnswers404_NothingStoredYet_Returns200_Defaults()
    {
        var stub = new CountingHandler(_ => NotFound());
        using var factory = NewFactory(stub);
        var client = ClientFor(factory, "w09f-np-firstcall");

        var resp = await client.GetAsync(NotificationPrefsRoute);
        var body = await resp.Content.ReadAsStringAsync();

        stub.Calls.Should().BeGreaterThan(0, "the remote store must actually be the one wired in");
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "'no prefs stored yet' is an answer: the defaults are the correct 200 body here");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("preferences").GetProperty("offers").GetBoolean().Should().BeTrue();
    }

    // ---- a healthy upstream is unchanged ----

    [Fact]
    public async Task SavedLocations_List_HealthyUpstream_ServesStoredItems_Unchanged()
    {
        var blob = "{\"items\":[{\"id\":\"abc\",\"label\":\"Home\",\"latitude\":33.9,\"longitude\":35.5,"
                   + "\"isDefault\":true,\"createdAt\":\"2026-08-01T00:00:00+00:00\",\"updatedAt\":\"2026-08-01T00:00:00+00:00\"}]}";
        var stub = new CountingHandler(_ => Ok("{\"value\":" + JsonSerializer.Serialize(blob) + "}"));
        using var factory = NewFactory(stub);
        var client = ClientFor(factory, "w09f-sl-healthy");

        var resp = await client.GetAsync(SavedLocationsRoute);
        var body = await resp.Content.ReadAsStringAsync();

        stub.Calls.Should().BeGreaterThan(0);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Home", "a healthy read is passed through exactly as before");
    }

    // ---- writes: an aborted pre-read is a fast 504, not an opaque 500 ----

    [Fact]
    public async Task SavedLocations_Create_UnansweredUpstream_Returns504_Not500()
    {
        var stub = new CountingHandler(_ => throw new HttpRequestException(
            "No route to host (simulated unreachable remote-user-preferences)"));
        using var factory = NewFactory(stub);
        var client = ClientFor(factory, "w09f-sl-create-dead");

        var resp = await client.PostAsync(SavedLocationsRoute, new StringContent(
            "{\"label\":\"Home\",\"latitude\":33.9,\"longitude\":35.5}", Encoding.UTF8, "application/json"));

        stub.Calls.Should().BeGreaterThan(0, "the remote store must actually be the one wired in");
        ((int)resp.StatusCode).Should().Be(504,
            "the write aborts on an unanswered pre-read — surface it as a retryable 504, not 'An unexpected error occurred.'");
        ((int)resp.StatusCode).Should().NotBe(500);
    }

    // ---- harness ----

    /// <summary>
    /// Wires the REAL RemoteUserPreferences-backed stores (the test env otherwise runs the
    /// in-memory ones) over a stubbed upstream client, so these probes exercise the live shape.
    /// </summary>
    private static WebApplicationFactory<Program> NewFactory(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceRemoteUserPreferencesClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://prefs.test/") };
                    return new ServiceRemoteUserPreferencesClient("http://prefs.test/", http);
                });

                services.RemoveAll<ISavedLocationStore>();
                services.AddSingleton<ISavedLocationStore>(sp => new RemoteUserPreferencesSavedLocationStore(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<ILogger<RemoteUserPreferencesSavedLocationStore>>()));

                services.RemoveAll<INotificationPreferencesStore>();
                services.AddSingleton<INotificationPreferencesStore>(sp =>
                    new RemoteUserPreferencesNotificationPreferencesStore(
                        sp.GetRequiredService<IServiceScopeFactory>(),
                        sp.GetRequiredService<ILogger<RemoteUserPreferencesNotificationPreferencesStore>>()));
            });
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        return client;
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent("{\"detail\":\"not found\"}", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>Counts sends so a test can prove the stubbed upstream was really reached —
    /// a probe that silently no-ops would otherwise report PASS.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private int _calls;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                return Task.FromResult(_respond(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
