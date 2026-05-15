using JeebGateway.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Jeeb Gateway",
        Version = "v1",
        Description = "BFF gateway aggregating downstream Jeeb services."
    });
});

// Health checks
builder.Services.AddHealthChecks();

// OpenTelemetry
var serviceName = "jeeb-gateway";
var otlpEndpoint = builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
    });

// Typed HttpClient registrations for downstream services will go here.
// Example:
// builder.Services.AddHttpClient<ISomeServiceClient, SomeServiceClient>();

// ---------------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------------

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Jeeb Gateway v1"));
}

app.UseRouting();
app.UseAuthorization();

app.MapControllers();

// Health endpoints — separate liveness and readiness probes.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // liveness: always 200 if process is up
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// Required for WebApplicationFactory<Program> integration tests.
public partial class Program { }
