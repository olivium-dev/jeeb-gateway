using System.Text;
using System.Security.Claims;
using FluentAssertions;
using JeebGateway.StateService.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class CaseIdempotencyMiddlewareTests
{
    [Fact]
    public async Task Exact_Dispute_Create_Replay_Returns_Original_201_Without_Reexecution()
    {
        var executions = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            executions++;
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsync("{\"id\":\"case-original\",\"result\":\"created\"}");
        }, NullLogger<IdempotencyMiddleware>.Instance);
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        const string body = "{\"deliveryId\":\"delivery-1\",\"reason\":\"damaged\",\"comment\":\"first\"}";

        var first = Context(body, "dispute-create-1");
        await middleware.InvokeAsync(first, store);
        var replay = Context(body, "dispute-create-1");
        await middleware.InvokeAsync(replay, store);

        executions.Should().Be(1);
        first.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        replay.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        replay.Response.Headers["Idempotency-Replayed"].ToString().Should().Be("true");
        (await ResponseBody(replay)).Should().Be(await ResponseBody(first));
    }

    [Fact]
    public async Task Same_Dispute_Create_Key_With_Changed_Body_Returns_409()
    {
        var executions = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            executions++;
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsync("{\"id\":\"case-original\"}");
        }, NullLogger<IdempotencyMiddleware>.Instance);
        var store = new InMemoryIdempotencyStore(TimeProvider.System);

        var first = Context(
            "{\"deliveryId\":\"delivery-1\",\"reason\":\"damaged\",\"comment\":\"first\"}",
            "dispute-create-2");
        await middleware.InvokeAsync(first, store);
        var changed = Context(
            "{\"deliveryId\":\"delivery-1\",\"reason\":\"damaged\",\"comment\":\"changed\"}",
            "dispute-create-2");
        await middleware.InvokeAsync(changed, store);

        executions.Should().Be(1, "the conflicting body must be rejected before the endpoint runs");
        changed.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await ResponseBody(changed)).Should().Contain("idempotency-key-reused");
    }

    [Fact]
    public async Task Same_Dispute_Create_Key_And_Body_Are_Isolated_By_Verified_Principal()
    {
        var executions = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            executions++;
            var principal = context.User.FindFirstValue("sub");
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsync($"{{\"id\":\"case-{principal}\"}}");
        }, NullLogger<IdempotencyMiddleware>.Instance);
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        const string body = "{\"deliveryId\":\"delivery-1\",\"reason\":\"damaged\"}";

        var firstUser = Context(body, "common-mobile-key", "user-a");
        await middleware.InvokeAsync(firstUser, store);
        var secondUser = Context(body, "common-mobile-key", "user-b");
        await middleware.InvokeAsync(secondUser, store);

        executions.Should().Be(2);
        firstUser.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        secondUser.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        secondUser.Response.Headers.Should().NotContainKey("Idempotency-Replayed");
        (await ResponseBody(firstUser)).Should().Contain("case-user-a");
        (await ResponseBody(secondUser)).Should().Contain("case-user-b");
    }

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

    private static DefaultHttpContext Context(
        string body,
        string key,
        string principal = "user-a")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", principal) }, "gateway-test"));
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/disputes";
        context.Request.Headers["Idempotency-Key"] = key;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
