using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace JeebGateway.IntegrationTests.ContractSigning;

/// <summary>
/// GW12-SEC-2 (JEBV4-84) — GET /contract-signing/contracts/{id} must be scoped
/// to a participant. Before the fix any authenticated user could read another
/// party's contract (parties, signatures, partyRef) by guessing/harvesting an
/// opaque id (OWASP API1:2023 BOLA). These controller-level tests assert the
/// SECURITY OUTCOME (403 for a non-participant, 200 for a party/admin, 401 for
/// an anonymous caller) rather than a mere 2xx.
/// </summary>
public class ContractGetParticipantScopingTests
{
    private const string OwnerId = "user_owner_42";
    private const string StrangerId = "user_stranger_99";

    [Fact]
    public async Task Party_Reads_Own_Contract_Returns_200()
    {
        var controller = NewController(flagOn: true, principal: PrincipalFor(OwnerId));

        var result = await controller.GetContract("ctr_9", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).Value.Should().BeOfType<Contract>();
    }

    [Fact]
    public async Task NonParticipant_Reading_Others_Contract_Returns_403_RFC7807()
    {
        var controller = NewController(flagOn: true, principal: PrincipalFor(StrangerId));

        var result = await controller.GetContract("ctr_9", CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problem = obj.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status403Forbidden);
        problem.Type.Should().Be("https://jeeb.dev/errors/contract-not-participant");
    }

    [Fact]
    public async Task Admin_NonParticipant_Reads_Contract_Returns_200()
    {
        var controller = NewController(flagOn: true, principal: PrincipalFor(StrangerId, isAdmin: true));

        var result = await controller.GetContract("ctr_9", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Unauthenticated_Caller_Returns_401()
    {
        // Anonymous principal + no trusted X-User-Id header → identity cannot be
        // resolved, so the read is rejected before any upstream call.
        var controller = NewController(flagOn: true, principal: new ClaimsPrincipal(new ClaimsIdentity()));

        var result = await controller.GetContract("ctr_9", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Flag_Off_Returns_503_UnchangedBehavior()
    {
        var controller = NewController(flagOn: false, principal: PrincipalFor(OwnerId));

        var result = await controller.GetContract("ctr_9", CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    private static ContractSigningController NewController(bool flagOn, ClaimsPrincipal principal)
    {
        var flags = new StaticFlagsMonitor(new UpstreamFeatureFlags { ContractSigning = flagOn });
        var controller = new ContractSigningController(new FakeContractSigningClient(), flags)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
        return controller;
    }

    private static ClaimsPrincipal PrincipalFor(string userId, bool isAdmin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, "admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>Returns a contract whose only party is <see cref="OwnerId"/>.</summary>
    private sealed class FakeContractSigningClient : IContractSigningServiceClient
    {
        public Task<Contract> GetContractAsync(string contractId, CancellationToken ct)
        {
            var doc = JsonDocument.Parse(
                $$"""
                { "contract_id": "{{contractId}}", "template_id": "tpl_123", "status": "OPEN",
                  "stage": "SIGNED", "parties": [ { "role_key": "client", "party_ref": "{{OwnerId}}" } ],
                  "signatures": [ { "role_key": "client", "party_ref": "{{OwnerId}}" } ] }
                """).RootElement.Clone();

            return Task.FromResult(new Contract
            {
                ContractId = contractId,
                TemplateId = "tpl_123",
                Status = "OPEN",
                Stage = "SIGNED",
                Document = doc,
            });
        }

        public Task<ContractTemplate> RegisterTemplateAsync(RegisterTemplateRequest request, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JsonElement> ListTemplatesAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ContractTemplate> GetTemplateAsync(string templateId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Contract> CreateContractAsync(CreateContractRequest request, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Signature> SignAsync(string contractId, SignRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class StaticFlagsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<UpstreamFeatureFlags>
    {
        public StaticFlagsMonitor(UpstreamFeatureFlags value) => CurrentValue = value;
        public UpstreamFeatureFlags CurrentValue { get; }
        public UpstreamFeatureFlags Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<UpstreamFeatureFlags, string?> listener) => null;
    }
}
