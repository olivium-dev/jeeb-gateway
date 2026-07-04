using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Services.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC2 — JSON writer for the aggregated <c>/health</c>
/// endpoint that names every failing downstream by check name.
///
/// Default ASP.NET Core <see cref="HealthCheckOptions"/> writes a single
/// status string ("Healthy"/"Degraded"/"Unhealthy") which forces operators
/// to chase the failing dependency from logs. AC2 wants the response itself
/// to identify the failing service so a sevops dashboard can render a
/// status-by-service panel.
///
/// Response shape:
/// <code>
/// {
///   "status": "Unhealthy",
///   "totalDurationMs": 12.4,
///   "checks": [
///     { "name": "delivery-service", "status": "Unhealthy", "description": "...", "durationMs": 3.1, "tags": ["ready","downstream"] }
///   ],
///   "failing": ["delivery-service"]
/// }
/// </code>
///
/// Tags carry through so a dashboard can filter to <c>downstream</c> checks.
/// HTTP status code is owned by <see cref="HealthCheckOptions.ResultStatusCodes"/>
/// (configured at MapHealthChecks); the writer only owns the payload.
/// </summary>
public static class AggregateHealthResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
    };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            writer.WriteString("status", report.Status.ToString());
            writer.WriteNumber("totalDurationMs", report.TotalDuration.TotalMilliseconds);

            writer.WritePropertyName("checks");
            writer.WriteStartArray();
            foreach (var (name, entry) in report.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("status", entry.Status.ToString());
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    writer.WriteString("description", entry.Description);
                }
                writer.WriteNumber("durationMs", entry.Duration.TotalMilliseconds);

                writer.WritePropertyName("tags");
                writer.WriteStartArray();
                foreach (var tag in entry.Tags)
                {
                    writer.WriteStringValue(tag);
                }
                writer.WriteEndArray();

                if (entry.Exception is not null)
                {
                    // F3: /health/ready and /health/aggregate are AllowAnonymous, so the
                    // per-check exception MESSAGE must not be serialized — Npgsql (and the
                    // URL-group probes) embed the DB/upstream host:port in ex.Message, leaking
                    // internal topology to unauthenticated callers. Emit only the exception
                    // TYPE name (e.g. "NpgsqlException") — enough for a red/green dashboard to
                    // see a fault class without disclosing hosts. The full exception (message +
                    // stack) stays SERVER-SIDE in the OTel span produced by HealthCheckPublisher.
                    writer.WriteString("error", entry.Exception.GetType().Name);
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("failing");
            writer.WriteStartArray();
            foreach (var (name, entry) in report.Entries)
            {
                if (entry.Status != HealthStatus.Healthy)
                {
                    writer.WriteStringValue(name);
                }
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return context.Response.Body.WriteAsync(buffer.ToArray()).AsTask();
    }
}
