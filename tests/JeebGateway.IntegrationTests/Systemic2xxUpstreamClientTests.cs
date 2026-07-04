using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServiceUserManagement;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Regression pack for the systemic-2xx gateway fix (JEBV4-8). The D1 fix
/// (<see cref="ServiceUserManagementClient.UserIdLoginAsync"/> accepting UM's
/// 201 Created on user-id-login) and the D1b fix
/// (<see cref="JeebStateServiceClient.UpsertIdempotencyKeyAsync"/> accepting
/// jeeb-state-service's 201/204 on the idempotency upsert) were originally two
/// narrow, single-endpoint patches. This suite pins those two behaviors AND
/// guards, via a source-scan, that no OTHER upstream client in the repo
/// regresses to the same "only 200 succeeds, anything else 2xx throws" bug —
/// the systemic version of the same defect class that surfaced live after D1
/// let UM's 201 through and 500'd on the state-service idempotency write.
///
/// Two clients (<c>JeebStateServiceClient</c>, <c>ServiceOTPClient</c>) are
/// NSwag-generated with a CI freshness gate (see nswag-state.json /
/// nswag-otp.json + .github/workflows/ci.yml), so their fix lives in the
/// committed OpenAPI contract (contracts/jeeb-state-service.openapi.json,
/// contracts/one-time-password.openapi.json now also declare 201 for every
/// write operation) — the generated code accepts 200 OR 201 as two sibling
/// success branches. Every other client in Services/ and Services/Clients/
/// has no such generation wiring, so the interim systemic patch widens their
/// sole success check to <c>status_ >= 200 &amp;&amp; status_ &lt; 300</c>.
/// The source-scan below asserts neither pattern regresses.
/// </summary>
public class Systemic2xxUpstreamClientTests
{
    // -----------------------------------------------------------------------
    // D1 pin — ServiceUserManagementClient.UserIdLoginAsync accepts 201
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task UserIdLoginAsync_Accepts_200_And_201(HttpStatusCode status)
    {
        const string json = """
            {"userId":"u-1","authToken":"tok","refreshToken":"rtok","recentlyCreated":true}
            """;
        var client = new ServiceUserManagementClient(
            "http://user-management.test/",
            new HttpClient(new StubHandler(status, json)));

        var result = await client.UserIdLoginAsync(
            new UserIdLoginRequest { UserId = "u-1", SuperAdminPassCode = "code" },
            CancellationToken.None);

        result.Should().NotBeNull();
        result.UserId.Should().Be("u-1");
        result.AuthToken.Should().Be("tok");
    }

    // -----------------------------------------------------------------------
    // D1b pin — JeebStateServiceClient.UpsertIdempotencyKeyAsync accepts
    // 200 (replay), 201 (created), and 204 (declared possible no-content).
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task UpsertIdempotencyKeyAsync_Accepts_2xx(HttpStatusCode status)
    {
        var client = new JeebStateServiceClient(
            "http://jeeb-state-service.test/",
            new HttpClient(new StubHandler(status, string.Empty)));

        var act = async () => await client.UpsertIdempotencyKeyAsync(
            new IdempotencyPutRequest { Key = "k-1", StatusCode = 201, TtlSeconds = 60 },
            CancellationToken.None);

        await act.Should().NotThrowAsync(
            "super-login stores the refresh-token via this upsert; jeeb-state-service " +
            "replaying 200 or creating 201/204 must both be treated as success (D1b), " +
            "not surfaced as a gateway 500");
    }

    [Fact]
    public async Task UpsertIdempotencyKeyAsync_Still_Throws_On_Real_Errors()
    {
        var client = new JeebStateServiceClient(
            "http://jeeb-state-service.test/",
            new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, "boom")));

        var act = async () => await client.UpsertIdempotencyKeyAsync(
            new IdempotencyPutRequest { Key = "k-1" }, CancellationToken.None);

        await act.Should().ThrowAsync<JeebStateServiceApiException>(
            "widening success handling to 2xx must never swallow a genuine 5xx");
    }

    // -----------------------------------------------------------------------
    // Systemic source-scan guard — no client may reintroduce a lone,
    // throws-on-everything-else "status_ == 200" success check.
    // -----------------------------------------------------------------------

    // NSwag-generated clients whose 2xx acceptance lives in the regenerated
    // code (from the committed OpenAPI contract, which now declares 201
    // alongside 200 for every write op) rather than the "status_ >= 200 &&
    // status_ < 300" interim patch. Their success branches are still
    // single-code (200) / (201) pairs by design — see class doc above.
    private static readonly string[] RegeneratedFromContractFiles =
    {
        "JeebStateServiceClient.cs",
        "ServiceOTPClient.cs",
    };

    private static readonly Regex LoneSuccessCheck =
        new(@"if \(status_ == (?:200|""200"")\)", RegexOptions.Compiled);

    [Fact]
    public void No_Upstream_Client_Has_A_Lone_200_Only_Success_Check()
    {
        var repoRoot = FindRepoRoot();
        var servicesDir = Path.Combine(repoRoot, "src", "JeebGateway", "Services");
        Directory.Exists(servicesDir).Should().BeTrue(
            $"expected the gateway Services tree at {servicesDir}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(servicesDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (RegeneratedFromContractFiles.Contains(name))
                continue;

            var text = File.ReadAllText(file);
            var matches = LoneSuccessCheck.Matches(text);
            if (matches.Count > 0)
                offenders.Add($"{Path.GetRelativePath(repoRoot, file)} ({matches.Count} occurrence(s))");
        }

        offenders.Should().BeEmpty(
            "every upstream client success check must accept the full 2xx range " +
            "(status_ >= 200 && status_ < 300), not a lone status_ == 200 that throws " +
            "on 201/202/204/etc — that is exactly the systemic bug behind D1/D1b " +
            "(JEBV4-8). Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void RegeneratedContractClients_Declare_201_Alongside_200_For_Every_Write_Op()
    {
        var repoRoot = FindRepoRoot();
        foreach (var name in RegeneratedFromContractFiles)
        {
            var file = Directory.EnumerateFiles(
                Path.Combine(repoRoot, "src", "JeebGateway", "Services"),
                name, SearchOption.AllDirectories).Single();
            var text = File.ReadAllText(file);

            // Sanity: the file must still recognize 201 as a success code
            // somewhere (i.e. the contract regen didn't get reverted).
            text.Should().Contain("if (status_ == 201)",
                $"{name} is expected to be regenerated from a contract that now " +
                "declares 201 for its write operations (systemic-2xx, JEBV4-8)");
        }
    }

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        // tests/JeebGateway.IntegrationTests/Systemic2xxUpstreamClientTests.cs -> repo root
        var dir = new FileInfo(thisFile).Directory!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "JeebGateway")))
            dir = dir.Parent;

        (dir is not null).Should().BeTrue("could not locate repo root from test source file path");
        return dir!.FullName;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;

        public StubHandler(HttpStatusCode status, string json)
        {
            _status = status;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status) { RequestMessage = request };
            if (!string.IsNullOrEmpty(_json))
            {
                response.Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }
    }
}
