using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.Kyc;

/// <summary>
/// S03 H8 root-fix at the client seam. The product-agnostic kyc-service does NOT
/// name the Jeeb role on its review response — verified against the LIVE service,
/// an approve returns <c>{"submission":{...,"status":"Verified"}}</c> with NO
/// <c>grantsRole</c> anywhere, and a reject returns <c>status:"Rejected"</c>. By
/// ARCH LAW only the gateway holds Jeeb vocabulary, so the gateway DERIVES the
/// role-grant intent from the outcome status: a review that lands on Verified is
/// an approve and grants the Jeeb (jeeber) role; every other outcome grants
/// nothing. These tests pin that derivation against the real wire shape, so the
/// grant can never silently regress to null (which skips the UM append → the
/// roleGranted:false bug).
/// </summary>
public sealed class KycServiceClientReviewGrantTests
{
    private const string ApproveEnvelope =
        "{\"submission\":{\"id\":\"sub-abc\",\"subject\":\"applicant-guid-1\"," +
        "\"status\":\"Verified\",\"submittedAt\":\"2026-06-06T13:00:00+00:00\"," +
        "\"reviewedAt\":\"2026-06-06T13:00:01+00:00\",\"reviewerId\":\"admin-1\"," +
        "\"resubmitSlots\":[]}}";

    private const string RejectEnvelope =
        "{\"submission\":{\"id\":\"sub-def\",\"subject\":\"applicant-guid-2\"," +
        "\"status\":\"Rejected\",\"rejectionReason\":\"id_document_illegible\"," +
        "\"submittedAt\":\"2026-06-06T13:00:00+00:00\",\"resubmitSlots\":[]}}";

    [Fact]
    public async Task Approve_Outcome_Derives_Jeeber_Grant_Intent_And_Reads_Subject()
    {
        var client = ClientReturning(HttpStatusCode.OK, ApproveEnvelope);

        var decision = await client.ReviewAsync("sub-abc", new KycReviewDecisionRequest
        {
            Action = KycReviewActionKind.Approve,
            ReviewerId = "admin-1",
        }, CancellationToken.None);

        decision.Status.Should().Be("Verified");
        // owner is read from the LIVE `subject` field, so the gateway can append.
        decision.UserId.Should().Be("applicant-guid-1");
        // grant intent DERIVED in the gateway from the Verified outcome.
        decision.GrantsRole.Should().Be("jeeber");
    }

    [Fact]
    public async Task Reject_Outcome_Derives_No_Grant_Intent()
    {
        var client = ClientReturning(HttpStatusCode.OK, RejectEnvelope);

        var decision = await client.ReviewAsync("sub-def", new KycReviewDecisionRequest
        {
            Action = KycReviewActionKind.Reject,
            Reason = "id_document_illegible",
            ReviewerId = "admin-1",
        }, CancellationToken.None);

        decision.Status.Should().Be("Rejected");
        decision.RejectionReason.Should().Be("id_document_illegible");
        // a non-approve outcome grants nothing — the gateway must not append a role.
        decision.GrantsRole.Should().BeNull();
    }

    private static KycServiceClient ClientReturning(HttpStatusCode status, string json)
    {
        var handler = new StubHandler(status, json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://kyc.test/") };
        return new KycServiceClient(http);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;

        public StubHandler(HttpStatusCode status, string json)
        {
            _status = status;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
