using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JeebGateway.Jobs;

public sealed class InternalJobAuthOptions
{
    public const string SectionName = "InternalJobAuth";
    public string HeaderName { get; init; } = "X-Jeeb-Job-Token";
    public string TokenFile { get; init; } = string.Empty;
}

/// <summary>
/// Dedicated deployment-service credential for scheduled executor calls. It is
/// intentionally separate from mobile/admin bearer authentication and from the
/// legacy broad internal API-key map.
/// </summary>
public sealed class InternalJobTokenAuthorizationFilter(
    Microsoft.Extensions.Options.IOptions<InternalJobAuthOptions> options,
    ILogger<InternalJobTokenAuthorizationFilter> logger) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.HeaderName)
            || string.IsNullOrWhiteSpace(configured.TokenFile)
            || !Path.IsPathFullyQualified(configured.TokenFile))
        {
            logger.LogError("Internal job service-token configuration is unavailable");
            context.Result = Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Executor authentication unavailable");
            return;
        }

        string expected;
        try
        {
            expected = (await File.ReadAllTextAsync(
                configured.TokenFile,
                context.HttpContext.RequestAborted)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Internal job service-token file is unavailable");
            context.Result = Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Executor authentication unavailable");
            return;
        }
        if (expected.Length < 32 || expected.Length > 4096 || expected.Any(char.IsWhiteSpace))
        {
            logger.LogError("Internal job service-token file contains an invalid credential");
            context.Result = Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Executor authentication unavailable");
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(configured.HeaderName, out var supplied)
            || supplied.Count != 1
            || string.IsNullOrWhiteSpace(supplied[0]))
        {
            context.Result = Problem(StatusCodes.Status401Unauthorized, "Executor service token required");
            return;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied[0]!);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (suppliedBytes.Length != expectedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        {
            context.Result = Problem(StatusCodes.Status403Forbidden, "Executor service token invalid");
        }
    }

    private static ObjectResult Problem(int status, string title) => new(new ProblemDetails
    {
        Type = $"https://httpstatuses.com/{status}",
        Title = title,
        Status = status
    })
    {
        StatusCode = status,
        ContentTypes = { "application/problem+json" }
    };
}
