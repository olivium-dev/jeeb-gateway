using System.Net;
using System.Text;
using JeebGateway.Requests.OtpHandover;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.UnitTests;

// STEP-10 control for the live dual-write-upstream-read flip: the upstream store must really
// reach delivery-service /api/v1/escalations and must fail CLOSED, never to an empty queue.
public class OtpEscalationReadRungTests
{
    private const string Row =
        "{\"escalation_id\":\"esc-1\",\"delivery_id\":\"d-1\",\"client_id\":\"c-1\","
        + "\"provider_id\":\"p-1\",\"reason\":\"otp_locked\",\"status\":\"pending\","
        + "\"attempt_count\":3,\"created_at\":\"2026-08-16T09:00:00+00:00\"}";

    private const string Body = "[" + Row + "]";

    [Fact]
    public async Task Read_Goes_To_Delivery_Service_And_Filters_By_Delivery_Id()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, Body);

        var found = await Store(handler).GetForDeliveryAsync("d-1", EscalationReason.OtpLocked, default);

        Assert.Equal("api/v1/escalations?delivery_id=d-1", handler.LastPath);
        Assert.NotNull(found);
        Assert.Equal("esc-1", found!.Id);
        Assert.Equal(3, found.OtpAttemptCount);
    }

    [Fact]
    public async Task A_Reason_That_Does_Not_Match_Is_Not_Reported_As_An_Escalation()
    {
        // Anti-vacuity: the same upstream body must NOT satisfy a different reason, otherwise the
        // assertion above would pass for any row delivery-service happened to return.
        var found = await Store(new CapturingHandler(HttpStatusCode.OK, Body))
            .GetForDeliveryAsync("d-1", EscalationReason.ClientUnreachable, default);

        Assert.Null(found);
    }

    [Fact]
    public async Task An_Upstream_Fault_Fails_Closed_Instead_Of_Showing_An_Empty_Queue()
    {
        var store = Store(new CapturingHandler(HttpStatusCode.InternalServerError, "boom"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => store.ListAsync(default));
    }

    [Fact]
    public async Task Create_Posts_The_Row_And_Accepts_The_Idempotent_Replay()
    {
        // delivery-service answers 200 (not 201) when the escalation_id was already written.
        var handler = new CapturingHandler(HttpStatusCode.OK, Row);

        var saved = await Store(handler).CreateAsync(
            new AdminEscalation
            {
                Id = "esc-1",
                DeliveryId = "d-1",
                ClientId = "c-1",
                JeeberId = "p-1",
                Reason = EscalationReason.OtpLocked,
                Status = EscalationStatus.Pending,
                CreatedAt = DateTimeOffset.UnixEpoch,
            },
            default);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("api/v1/escalations", handler.LastPath);
        Assert.Contains("\"escalation_id\":\"esc-1\"", handler.LastRequestBody);
        // G-28 holder-generic wire name: the gateway's jeeberId travels as provider_id.
        Assert.Contains("\"provider_id\":\"p-1\"", handler.LastRequestBody);
        Assert.Equal("esc-1", saved.Id);
    }

    [Fact]
    public void Program_Binds_The_Read_Rung_And_Refuses_An_Unwired_Upstream()
    {
        var program = ReadProgram();

        // Anti-vacuity: the same search must MISS a name that is genuinely absent.
        Assert.DoesNotContain("NeverBoundEscalationStore", program, StringComparison.Ordinal);

        Assert.Contains("PhaseOf(builder.Configuration[\"FeatureFlags:OtpEscalationsMode\"])", program, StringComparison.Ordinal);
        Assert.Contains("new JeebGateway.Requests.OtpHandover.DeliveryServiceAdminEscalationStore(", program, StringComparison.Ordinal);
        Assert.Contains("RequiresUpstream(o.OtpEscalations) || gwdbxDeliveryWired", program, StringComparison.Ordinal);
    }

    private static string ReadProgram()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src/JeebGateway/Program.cs")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src/JeebGateway/Program.cs"));
    }

    private static DeliveryServiceAdminEscalationStore Store(CapturingHandler handler)
        => new(
            new SingleClientFactory(new HttpClient(handler)
            {
                BaseAddress = new Uri("http://delivery.test/"),
            }),
            NullLogger<DeliveryServiceAdminEscalationStore>.Instance);

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri!.PathAndQuery.TrimStart('/');
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
