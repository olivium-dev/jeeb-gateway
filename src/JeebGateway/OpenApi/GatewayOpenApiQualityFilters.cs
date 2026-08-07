using System.Text;
using JeebGateway.Admin;
using JeebGateway.Cases;
using JeebGateway.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace JeebGateway.OpenApi;

/// <summary>Publishes the browser's same-origin gateway mount, never an internal host.</summary>
public sealed class GatewayServerDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        swaggerDoc.Servers = new List<OpenApiServer>
        {
            new() { Url = "/gateway", Description = "Same-origin Jeeb gateway" },
        };
    }
}

/// <summary>
/// Guarantees a concise summary for generated clients and API review. Explicit
/// endpoint metadata wins; otherwise the controller action name is humanized.
/// </summary>
public sealed class GatewayOperationSummaryFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (string.IsNullOrWhiteSpace(operation.Summary))
        {
            var declared = context.MethodInfo
                .GetCustomAttributes(typeof(EndpointSummaryAttribute), inherit: true)
                .OfType<EndpointSummaryAttribute>()
                .Select(attribute => attribute.Summary)
                .FirstOrDefault(summary => !string.IsNullOrWhiteSpace(summary));
            if (!string.IsNullOrWhiteSpace(declared))
                operation.Summary = declared.Trim();
            else
            {
                var controller = context.MethodInfo.DeclaringType?.Name ?? "Gateway";
                controller = controller.EndsWith("Controller", StringComparison.Ordinal)
                    ? controller[..^"Controller".Length]
                    : controller;
                operation.Summary = $"{Humanize(controller)}: {Humanize(context.MethodInfo.Name)}";
            }
        }

        if (string.IsNullOrWhiteSpace(operation.OperationId))
        {
            var source = string.Join('_',
                context.MethodInfo.DeclaringType?.Name ?? "Gateway",
                context.MethodInfo.Name,
                context.ApiDescription.HttpMethod ?? "Any",
                context.ApiDescription.RelativePath ?? "root");
            operation.OperationId = SanitizeIdentifier(source);
        }

        if (context.MethodInfo.DeclaringType == typeof(AdminCasesController)
            && context.MethodInfo.Name == nameof(AdminCasesController.LegacyResolve))
        {
            operation.Description =
                "Compatibility-only case resolution route. Jeeb is cash on delivery: " +
                "refundUsd and refund actions are rejected, and any approved cash reimbursement " +
                "is arranged manually after review.";
        }

        // Every route can reject malformed client input with 400. Use a
        // concrete status because NSwag 14.2 emits invalid TypeScript for the
        // otherwise-valid OpenAPI ranged key "4XX".
        if (!operation.Responses.Keys.Any(static code =>
                code.Length > 0 && code[0] == '4'))
            operation.Responses["400"] = new OpenApiResponse
            {
                Description = "Client request rejected.",
            };
    }

    private static string Humanize(string value)
    {
        var output = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character)
                          && (char.IsLower(value[index - 1])
                              || index + 1 < value.Length && char.IsLower(value[index + 1])))
                output.Append(' ');
            output.Append(index == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character));
        }
        return output.ToString();
    }

    private static string SanitizeIdentifier(string value)
    {
        var output = new StringBuilder(value.Length);
        var previousSeparator = false;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                output.Append(character);
                previousSeparator = false;
            }
            else if (!previousSeparator)
            {
                output.Append('_');
                previousSeparator = true;
            }
        }
        return output.ToString().Trim('_');
    }
}

/// <summary>Repairs the OpenAPI 3.0 shape for nullable arbitrary JSON evidence.</summary>
public sealed class GatewayEvidenceSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(GenericCaseEvidenceV1))
            RepairArbitraryJson(schema, "payload");
        else if (context.Type == typeof(AdminDeliveryEvidenceAggregate))
            foreach (var property in new[]
                     {
                         "proof", "route", "latestCourierLocation", "codSettlements", "chat",
                     })
                RepairArbitraryJson(schema, property);
        else if (context.Type == typeof(AdminDeliveryTimelineEntry))
            RepairArbitraryJson(schema, "geo");
        else if (context.Type == typeof(AdminSettlementBatchResource))
            RepairArbitraryJson(schema, "metadata");
        else if (context.Type == typeof(SettlementEventCallbackV1))
            RepairArbitraryJson(schema, "commission_rate");
        else if (context.Type == typeof(SettlementEventMoneyV1))
            foreach (var property in new[] { "gross_amount", "commission_amount", "net_amount" })
                RepairArbitraryJson(schema, property);
        else if (context.Type == typeof(LegacyCaseResolutionRequest))
        {
            schema.Description =
                "Compatibility input for non-financial case closure. Automated refund and " +
                "card-chargeback actions are not supported for Jeeb COD.";
            if (schema.Properties.TryGetValue("refundUsd", out var legacyRefund))
                legacyRefund.Description =
                    "Rejected legacy input. Approved COD cash reimbursement is arranged manually.";
        }
    }

    private static void RepairArbitraryJson(OpenApiSchema schema, string property)
    {
        if (!schema.Properties.TryGetValue(property, out var arbitraryJson)) return;
        arbitraryJson.Type = "object";
        arbitraryJson.Nullable = true;
        arbitraryJson.AdditionalPropertiesAllowed = true;
    }
}
