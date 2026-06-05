using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace JeebGateway.Security;

/// <summary>
/// Swashbuckle operation filter that renders actions which bind
/// <see cref="IFormFile"/> parameters via <c>[FromForm]</c> as a
/// <c>multipart/form-data</c> request body, instead of letting the default
/// generator choke on "[FromForm] attribute used with IFormFile".
///
/// <para>
/// Why this exists: <see cref="JeebGateway.Controllers.KycController"/>'s
/// <c>POST /kyc/submit</c> binds three <c>[FromForm] IFormFile?</c> params plus
/// scalar form fields. Swashbuckle 6.x throws a
/// <c>SwaggerGeneratorException</c> when it encounters an <c>IFormFile</c> bound
/// with an explicit <c>[FromForm]</c> attribute (it expects the file to flow
/// through the multipart request body, not as a discrete parameter). That
/// exception surfaced as a 500 on <c>/swagger/v1/swagger.json</c> the moment the
/// admin-gated Swagger surface was enabled under Production — Swagger had never
/// actually rendered before because it was off on the live host.
/// </para>
///
/// <para>
/// This filter detects such operations, drops the individual file/scalar
/// parameters from the parameter list, and emits an explicit multipart schema
/// (binary <c>format</c> for files) on the request body so the document
/// generates cleanly and accurately describes the upload contract. It is purely
/// a documentation-generation concern — the controller's runtime model binding
/// is untouched.
/// </para>
/// </summary>
public sealed class MultipartFormFileOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var formFileParams = context.ApiDescription.ParameterDescriptions
            .Where(p => p.ModelMetadata?.ModelType == typeof(IFormFile)
                        || p.ModelMetadata?.ModelType == typeof(IFormFileCollection)
                        || (p.ModelMetadata?.ElementType == typeof(IFormFile)))
            .ToList();

        if (formFileParams.Count == 0)
        {
            return;
        }

        // Every form-bound parameter on this action becomes a property of the
        // multipart schema. Files are binary strings; everything else is a plain
        // string field (the gateway reads scalar form fields as strings).
        var formParams = context.ApiDescription.ParameterDescriptions
            .Where(p => p.Source?.Id == "Form" || p.Source?.Id == "FormFile")
            .ToList();

        var schema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>(),
            Required = new HashSet<string>(),
        };

        foreach (var p in formParams)
        {
            var isFile = p.ModelMetadata?.ModelType == typeof(IFormFile)
                         || p.ModelMetadata?.ElementType == typeof(IFormFile);
            schema.Properties[p.Name] = isFile
                ? new OpenApiSchema { Type = "string", Format = "binary" }
                : new OpenApiSchema { Type = "string" };
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType { Schema = schema },
            },
        };

        // Remove the now-duplicated discrete parameters so Swashbuckle does not
        // also try (and fail) to render them as query/path parameters.
        if (operation.Parameters is not null)
        {
            var formNames = formParams.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            operation.Parameters = operation.Parameters
                .Where(p => !formNames.Contains(p.Name))
                .ToList();
        }
    }
}
