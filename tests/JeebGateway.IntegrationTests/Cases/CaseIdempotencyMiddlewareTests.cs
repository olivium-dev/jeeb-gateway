using FluentAssertions;
using JeebGateway.StateService.Idempotency;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class CaseIdempotencyMiddlewareTests
{
    [Theory]
    [InlineData("/v1/disputes")]
    [InlineData("/v1/disputes/case-id/reply")]
    [InlineData("/v1/support/tickets")]
    [InlineData("/v1/support/tickets/case-id/messages")]
    [InlineData("/v1/deliveries/delivery-id/escalate")]
    [InlineData("/admin/v1/cases/case-id/close")]
    [InlineData("/admin/v1/disputes/case-id/resolve")]
    [InlineData("/admin/v1/support/tickets/case-id/reply")]
    [InlineData("/deliveries/delivery-id/dispute")]
    [InlineData("/admin/disputes/case-id/resolve")]
    public void Case_mutations_bypass_key_only_gateway_response_cache(string path)
    {
        IdempotencyMiddleware.IsCaseMutation(new PathString(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("/admin/v1/cases")]
    [InlineData("/requests")]
    public void Unrelated_mutation_routes_keep_their_existing_policy(string path)
    {
        IdempotencyMiddleware.IsCaseMutation(new PathString(path)).Should().BeFalse();
    }
}
