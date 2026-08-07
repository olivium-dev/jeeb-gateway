using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace JeebGateway.Security;

/// <summary>
/// Documents the gateway's fallback authorization policy. Every controller
/// operation is bearer-protected unless its action or controller explicitly
/// opts out with <see cref="AllowAnonymousAttribute"/>.
/// </summary>
public sealed class BearerSecurityOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.MethodInfo;
        var allowsAnonymous = method.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any()
                              || (method.DeclaringType?.GetCustomAttributes(true)
                                      .OfType<IAllowAnonymous>().Any() ?? false);
        if (allowsAnonymous)
        {
            // OpenAPI inheritance is explicit: an empty array means this one
            // operation requires no scheme, even if document/path security is
            // introduced later. OpenAPI.NET omits empty optional collections,
            // so the response adapter replaces this marker with `security: []`.
            operation.Security = new List<OpenApiSecurityRequirement>();
            operation.Extensions["x-jeeb-explicit-anonymous"] = new OpenApiBoolean(true);
            return;
        }

        operation.Security = new List<OpenApiSecurityRequirement>
        {
            new()
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer",
                    },
                }] = Array.Empty<string>(),
            },
        };
    }
}
