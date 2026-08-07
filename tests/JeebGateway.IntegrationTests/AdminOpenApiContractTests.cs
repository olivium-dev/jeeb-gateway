using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class AdminOpenApiContractTests
{
    [Fact]
    public async Task RuntimeDocumentIsAuthoritativeForCookieAuthAndEssentialAdminModels()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");

        root.GetProperty("servers").EnumerateArray()
            .Select(server => server.GetProperty("url").GetString())
            .Should().Equal("/gateway");

        root.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString().Should().Be("bearer");

        paths.GetProperty("/admin/v1/auth/refresh").GetProperty("post")
            .TryGetProperty("requestBody", out _).Should().BeFalse();
        paths.GetProperty("/admin/v1/auth/logout").GetProperty("post")
            .TryGetProperty("requestBody", out _).Should().BeFalse();
        paths.TryGetProperty("/admin/v1/auth/login", out _).Should().BeFalse(
            "administrator login is OIDC-only and password credentials are not a gateway route");
        paths.GetProperty("/admin/v1/auth/refresh").GetProperty("post")
            .GetProperty("security").GetArrayLength().Should().Be(0);
        paths.GetProperty("/admin/v1/auth/logout").GetProperty("post")
            .GetProperty("security").GetArrayLength().Should().Be(0);
        paths.GetProperty("/admin/v1/auth/oidc/start").GetProperty("get")
            .GetProperty("security").GetArrayLength().Should().Be(0);
        paths.GetProperty("/admin/v1/auth/oidc/callback").GetProperty("get")
            .GetProperty("security").GetArrayLength().Should().Be(0);
        paths.GetProperty("/admin/v1/session").GetProperty("get")
            .GetProperty("security").GetArrayLength().Should().Be(1);
        paths.EnumerateObject().Select(property => property.Name).Should().Contain(
            "/admin/v1/auth/oidc/start",
            "/admin/v1/auth/oidc/callback",
            "/admin/v1/session",
            "/admin/v1/cases",
            "/admin/v1/cases/{id}",
            "/admin/v1/deliveries",
            "/admin/v1/deliveries/{deliveryId}",
            "/admin/v1/deliveries/{deliveryId}/transition",
            "/admin/v1/settlements",
            "/admin/v1/settlements/{settlementId}/dispute",
            "/admin/v1/settlements/{settlementId}/resolve",
            "/admin/v1/settlement-batches/{batchId}/mark-paid");
        // Extraction adaptation: the legacy /v1/admin/settlements/batches routes
        // deliberately remain alongside the new /admin/v1 surface on this branch.
        var adminOperations = paths.EnumerateObject()
            .Where(path => path.Name.StartsWith("/admin/", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Name is "get" or "post" or "put" or "patch" or "delete"));
        adminOperations.All(operation =>
            operation.Value.TryGetProperty("summary", out var summary)
            && !string.IsNullOrWhiteSpace(summary.GetString()))
            .Should().BeTrue("every admin operation must carry a generated summary");

        var schemas = root.GetProperty("components").GetProperty("schemas");
        schemas.TryGetProperty("AdminLoginRequest", out _).Should().BeFalse();
        var legacyResolve = paths.GetProperty("/admin/v1/disputes/{id}/resolve").GetProperty("post");
        legacyResolve.GetProperty("description").GetString().Should().Contain("refundUsd and refund actions are rejected");
        schemas.GetProperty("LegacyCaseResolutionRequest").GetProperty("description").GetString()
            .Should().Contain("Automated refund and card-chargeback actions are not supported");
        schemas.GetProperty("LegacyCaseResolutionRequest").GetProperty("properties")
            .GetProperty("refundUsd").GetProperty("description").GetString()
            .Should().Contain("Rejected legacy input");
        schemas.GetProperty("AdminAccessTokenResponse").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name)
            .Should().Equal("accessToken");
        schemas.GetProperty("CaseDetailResponseV2").GetProperty("properties")
            .TryGetProperty("participantRefs", out _).Should().BeTrue();
        var evidencePayload = schemas.GetProperty("GenericCaseEvidenceV1")
            .GetProperty("properties").GetProperty("payload");
        evidencePayload.GetProperty("type").GetString().Should().Be("object");
        evidencePayload.GetProperty("nullable").GetBoolean().Should().BeTrue();
        schemas.EnumerateObject().Select(property => property.Name).Should().Contain(
            "AdminDeliveryListResponse",
            "AdminDeliveryAggregateResponse",
            "AdminDeliveryTransitionRequest",
            "AdminDeliveryTransitionResponse",
            "AdminSettlementPageResponse",
            "AdminSettlementDetailResponse",
            "AdminSettlementReconcileResponse",
            "AdminDisputeSettlementRequest",
            "AdminResolveSettlementRequest");

        ResponseSchema(paths, "/admin/v1/cases/{id}", "get").Should().EndWith("/CaseDetailResponseV2");
        ResponseSchema(paths, "/admin/v1/deliveries/{deliveryId}", "get")
            .Should().EndWith("/AdminDeliveryAggregateResponse");
        ResponseSchema(paths, "/admin/v1/deliveries/{deliveryId}/transition", "post")
            .Should().EndWith("/AdminDeliveryTransitionResponse");
        ResponseSchema(paths, "/admin/v1/settlements", "get")
            .Should().EndWith("/AdminSettlementPageResponse");

        QueryParameterNames(paths, "/admin/v1/deliveries", "get").Should().BeEquivalentTo(
            "query", "status", "clientId", "jeeberId", "createdFrom", "createdTo", "sort", "limit", "cursor");
        QueryParameterNames(paths, "/admin/v1/deliveries/{deliveryId}/timeline", "get")
            .Should().BeEquivalentTo("deliveryId", "limit", "cursor");
        QueryParameterNames(paths, "/admin/v1/settlements", "get").Should().BeEquivalentTo(
            "query", "status", "providerId", "deliveryId", "from", "to", "sort", "limit", "cursor");

        var transition = paths.GetProperty("/admin/v1/deliveries/{deliveryId}/transition")
            .GetProperty("post");
        transition.GetProperty("responses").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("200", "400", "403", "404", "409", "422", "502");
        var idempotency = transition.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key")
            .GetProperty("schema");
        idempotency.GetProperty("minLength").GetInt32().Should().Be(8);
        idempotency.GetProperty("maxLength").GetInt32().Should().Be(200);
        schemas.GetProperty("AdminDeliveryTransitionRequest").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name)
            .Should().Equal("to", "expected_status", "reason");
        schemas.GetProperty("AdminDeliveryTransitionResponse").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name)
            .Should().Equal("delivery_id", "previous_status", "status", "transition_id", "transitioned_at");
    }

    private static string ResponseSchema(JsonElement paths, string path, string method) =>
        paths.GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString()!;

    private static IEnumerable<string> QueryParameterNames(
        JsonElement paths,
        string path,
        string method) =>
        paths.GetProperty(path).GetProperty(method).GetProperty("parameters")
            .EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()!);
}
