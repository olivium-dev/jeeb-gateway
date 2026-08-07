using System.Text.Json;
using System.Text.Json.Nodes;

namespace JeebGateway.OpenApi;

/// <summary>
/// OpenAPI.NET omits empty optional collections when serializing, but OpenAPI
/// uses an explicit <c>security: []</c> to override inherited security. This
/// narrow serializer adapter replaces the anonymous marker emitted by the
/// operation filter with the standards-significant empty array.
/// </summary>
public sealed class ExplicitAnonymousOpenApiSecurityMiddleware
{
    private const string Marker = "x-jeeb-explicit-anonymous";
    private static readonly HashSet<string> OperationNames = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "delete", "patch", "head", "options", "trace",
    };
    private readonly RequestDelegate _next;

    public ExplicitAnonymousOpenApiSecurityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals("/swagger/v1/swagger.json", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var destination = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context);
            buffer.Position = 0;
            if (context.Response.StatusCode != StatusCodes.Status200OK)
            {
                await buffer.CopyToAsync(destination, context.RequestAborted);
                return;
            }

            var document = await JsonNode.ParseAsync(buffer, cancellationToken: context.RequestAborted)
                           ?? throw new InvalidOperationException("The generated OpenAPI document is empty.");
            if (document["paths"] is JsonObject paths)
            {
                foreach (var path in paths.Select(entry => entry.Value).OfType<JsonObject>())
                foreach (var (name, node) in path)
                {
                    if (!OperationNames.Contains(name) || node is not JsonObject operation
                                                       || operation[Marker]?.GetValue<bool>() != true) continue;
                    operation.Remove(Marker);
                    operation["security"] = new JsonArray();
                }
            }

            context.Response.ContentLength = null;
            context.Response.Body = destination;
            await JsonSerializer.SerializeAsync(
                destination,
                document,
                new JsonSerializerOptions { WriteIndented = true },
                context.RequestAborted);
        }
        finally
        {
            context.Response.Body = destination;
        }
    }
}
