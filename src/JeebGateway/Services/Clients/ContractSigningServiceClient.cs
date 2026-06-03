using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IContractSigningServiceClient"/>.
/// Targets contract-signing-service's template/contract/signature routes
/// (FastAPI, api_prefix <c>/v1</c>). The named "contract-signing" HttpClient
/// (registered in <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>)
/// supplies the BaseAddress + the org-standard bearer / X-Service-Auth /
/// resilience chain, so this class never has to think about
/// retry/timeout/circuit-breaker.
///
/// contract-signing-service is a FastAPI app whose JSON envelope is snake_case
/// (<c>template_id</c>, <c>role_key</c>, <c>party_ref</c>,
/// <c>signature_proof_ref</c>, <c>stage_model</c>, <c>signed_at</c>). All binding
/// AND serialization therefore use <see cref="JsonNamingPolicy.SnakeCaseLower"/>
/// so request bodies match the upstream Pydantic models and responses bind
/// correctly. The variable, configuration-driven template/contract bodies are
/// additionally carried through verbatim as <see cref="JsonElement"/>
/// (<c>Document</c>) so the gateway never couples to their full shape.
/// </summary>
public sealed class ContractSigningServiceClient : IContractSigningServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public ContractSigningServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ContractTemplate> RegisterTemplateAsync(
        RegisterTemplateRequest request,
        CancellationToken ct)
    {
        // POST /v1/templates — register an immutable template (e.g. jeeb_tos_v1).
        using var response = await _http.PostAsJsonAsync("v1/templates", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return ToTemplate(document);
    }

    public async Task<JsonElement> ListTemplatesAsync(CancellationToken ct)
    {
        // GET /v1/templates — the PostgreSQL-backed template catalog (paginated
        // { "items": [...], "total", "limit", "offset" } envelope). NOTE: this is
        // the COLLECTION route, distinct from GET /v1/templates/{template_id};
        // the upstream resolves "/v1/templates/list" to the {id} route (id="list"
        // -> 404), so the list MUST hit the bare "/v1/templates" path.
        using var response = await _http.GetAsync("v1/templates", ct);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return document.Clone();
    }

    public async Task<ContractTemplate> GetTemplateAsync(string templateId, CancellationToken ct)
    {
        // GET /v1/templates/{template_id}
        var path = $"v1/templates/{Uri.EscapeDataString(templateId)}";
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return ToTemplate(document);
    }

    public async Task<Contract> CreateContractAsync(CreateContractRequest request, CancellationToken ct)
    {
        // POST /v1/contracts — instantiate a contract from a template.
        using var response = await _http.PostAsJsonAsync("v1/contracts", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return ToContract(document);
    }

    public async Task<Contract> GetContractAsync(string contractId, CancellationToken ct)
    {
        // GET /v1/contracts/{contract_id}
        var path = $"v1/contracts/{Uri.EscapeDataString(contractId)}";
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return ToContract(document);
    }

    public async Task<Signature> SignAsync(string contractId, SignRequest request, CancellationToken ct)
    {
        // POST /v1/contracts/{contract_id}/signatures — record a party signature.
        var path = $"v1/contracts/{Uri.EscapeDataString(contractId)}/signatures";
        using var response = await _http.PostAsJsonAsync(path, request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var signature = await response.Content.ReadFromJsonAsync<Signature>(JsonOptions, ct);
        return signature ?? new Signature { ContractId = contractId };
    }

    // --- mapping helpers ---

    private static ContractTemplate ToTemplate(JsonElement document) => new()
    {
        TemplateId = ReadString(document, "template_id"),
        Name = ReadString(document, "name"),
        Status = ReadString(document, "status"),
        StageModel = ReadStringList(document, "stage_model"),
        Document = document.Clone(),
    };

    private static Contract ToContract(JsonElement document) => new()
    {
        ContractId = ReadString(document, "contract_id"),
        TemplateId = ReadString(document, "template_id"),
        Status = ReadString(document, "status"),
        Stage = ReadString(document, "stage"),
        Document = document.Clone(),
    };

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string>? ReadStringList(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (s is not null) list.Add(s);
            }
        }

        return list;
    }
}
