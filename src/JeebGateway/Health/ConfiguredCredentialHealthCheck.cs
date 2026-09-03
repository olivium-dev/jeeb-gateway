using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Health;

/// <summary>
/// Readiness gate over one <see cref="GatewayCredentialDeclaration"/>. It walks the
/// declared chain exactly as the runtime handler does, so a credential that fails
/// closed at request time cannot report green here. It also names the rung that
/// actually resolved, which is what makes a value-backed fallback masking an
/// unmounted secret file visible instead of silently green.
/// </summary>
public sealed class ConfiguredCredentialHealthCheck(
    GatewayCredentialDeclaration declaration,
    IConfiguration configuration) : IHealthCheck
{
    internal const int MaximumTokenBytes = 4096;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!declaration.IsArmed(configuration))
        {
            return HealthCheckResult.Healthy(
                $"not armed ({declaration.ArmedDescription} is false): nothing dials with this credential");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        var configured = new List<string>();
        var faults = new List<string>();
        try
        {
            foreach (var source in declaration.Chain)
            {
                var raw = configuration[source.ConfigurationKey];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                configured.Add(source.ConfigurationKey);
                var fault = source.Kind == GatewayCredentialSourceKind.SecretFile
                    ? await InspectFileAsync(source.ConfigurationKey, raw, budget.Token)
                    : InspectValue(source.ConfigurationKey, raw);
                if (fault is null)
                {
                    return faults.Count == 0
                        ? HealthCheckResult.Healthy($"resolved from {source.ConfigurationKey}")
                        : HealthCheckResult.Degraded(
                            $"resolved from the fallback {source.ConfigurationKey}, masking: "
                            + string.Join("; ", faults));
                }

                faults.Add(fault);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"{declaration.Name}: resolution exceeded the {Budget.TotalSeconds:0}s readiness budget");
        }

        var chain = string.Join(" -> ", declaration.Chain.Select(s => s.ConfigurationKey));
        if (configured.Count == 0)
        {
            return HealthCheckResult.Degraded(
                $"no source configured while armed ({declaration.ArmedDescription}); "
                + $"the deploy must supply one of: {chain}");
        }

        return HealthCheckResult.Unhealthy(
            $"armed ({declaration.ArmedDescription}) but unresolvable. {string.Join("; ", faults)}. "
            + $"Resolution chain: {chain}");
    }

    private static async Task<string?> InspectFileAsync(
        string key,
        string path,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return $"{key} is not an absolute path";
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return $"{key} names an unusable path";
        }

        if (!info.Exists)
        {
            return $"{key} names {path}, which does not exist on this host";
        }

        if (info.Length is < 1 or > MaximumTokenBytes + 2)
        {
            return $"{key} names {path}, whose size is outside the allowed range";
        }

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true);
            if (stream.ReadByte() < 0)
            {
                return $"{key} names {path}, which is empty";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{key} names {path}, which could not be read";
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static string? InspectValue(string key, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumTokenBytes)
        {
            return $"{key} holds a value outside the allowed length";
        }

        return trimmed.Any(char.IsWhiteSpace)
            ? $"{key} holds a value containing whitespace"
            : null;
    }
}
