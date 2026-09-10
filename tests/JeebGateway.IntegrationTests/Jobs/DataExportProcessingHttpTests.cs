using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using JeebGateway.Users.DataExport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

/// <summary>Real MVC/auth-filter pipeline in memory; never boots gateway jobs or contacts an owner.</summary>
public sealed class DataExportProcessingHttpTests
{
    [Theory]
    [InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("wrong", HttpStatusCode.Forbidden)]
    [InlineData("duplicate", HttpStatusCode.Unauthorized)]
    [InlineData("valid", HttpStatusCode.ServiceUnavailable)]
    public async Task Manual_sweep_preserves_authentication_then_reports_pause_without_state_calls(
        string credential, HttpStatusCode expected)
    {
        const string synthetic = "synthetic-local-job-token-not-live-1234567890";
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(file, synthetic);
        try
        {
            var work = DispatchProxy.Create<IStateWorkItemClient, NoOwnerCalls>();
            var options = Options.Create(new DataExportOptions { Enabled = true, ProcessingEnabled = false });
            var executor = new DurableWorkSweepExecutor(work, [], Options.Create(new DurableWorkExecutionOptions()),
                TimeProvider.System, NullLogger<DurableWorkSweepExecutor>.Instance, new DataExportProcessingPolicy(options));
            using var server = new TestServer(new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddAuthorization();
                    services.AddControllers().AddApplicationPart(typeof(InternalDurableJobsController).Assembly)
                        .ConfigureApplicationPartManager(parts => parts.FeatureProviders.Add(new OnlyInternalJobs()));
                    services.AddSingleton(executor);
                    services.AddSingleton<IOptions<InternalJobAuthOptions>>(Options.Create(new InternalJobAuthOptions { TokenFile = file }));
                    services.AddScoped<InternalJobTokenAuthorizationFilter>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }));
            using var client = server.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/jobs/data-exports/sweep?limit=20");
            if (credential == "valid") request.Headers.Add("X-Jeeb-Job-Token", synthetic);
            if (credential == "wrong") request.Headers.Add("X-Jeeb-Job-Token", "incorrect-synthetic-value");
            if (credential == "duplicate") request.Headers.Add("X-Jeeb-Job-Token", new[] { synthetic, synthetic });
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(expected);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain(synthetic);
            if (credential == "valid")
            {
                response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
                using var json = JsonDocument.Parse(body);
                json.RootElement.GetProperty("type").GetString().Should().Be("urn:jeeb:data-export-processing-paused");
                json.RootElement.GetProperty("detail").GetString().Should().Contain("No work was claimed");
            }
            ((NoOwnerCalls)(object)work).Calls.Should().Be(0);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public class NoOwnerCalls : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException("An owner call is forbidden in this paused test.");
        }
    }

    private sealed class OnlyInternalJobs : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(type => type.AsType() != typeof(InternalDurableJobsController)).ToArray())
                feature.Controllers.Remove(controller);
        }
    }
}
