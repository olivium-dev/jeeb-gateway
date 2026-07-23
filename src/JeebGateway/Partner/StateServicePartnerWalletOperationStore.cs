using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.Partner;

/// <summary>
/// Stateless gateway adapter for partner-wallet idempotency. Every claim, retry marker,
/// completion, and uncertain outcome is persisted in jeeb-state-service's atomic
/// insert-or-return KV; the gateway keeps no row, cache, or database connection.
/// </summary>
public sealed class StateServicePartnerWalletOperationStore : IPartnerWalletOperationStore
{
    private const int TtlSeconds = 7 * 365 * 24 * 60 * 60;
    private const int MaxClaimGenerations = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _state;
    private readonly ILogger<StateServicePartnerWalletOperationStore> _log;

    public StateServicePartnerWalletOperationStore(
        IIdempotencyStore state,
        ILogger<StateServicePartnerWalletOperationStore> log)
    {
        _state = state;
        _log = log;
    }

    public async Task<PartnerOperationClaim> TryClaimAsync(
        PartnerOperationKey key, PartnerOperationIntent intent, CancellationToken ct)
    {
        var root = RootKey(key);
        var intentJson = JsonSerializer.Serialize(intent, Json);
        var first = await _state.PutOrGetAsync(root, 202, intentJson, TtlSeconds, ct);
        if (first.Inserted)
        {
            return new PartnerOperationClaim(PartnerClaimKind.Won, null);
        }

        if (!IntentMatches(first.ResponseBodyJson, intent))
        {
            _log.LogWarning(
                "Partner wallet idempotency collision for {Root}: request intent differs; refusing execution.",
                root);
            return new PartnerOperationClaim(PartnerClaimKind.InFlight, null);
        }

        var completed = await _state.GetAsync(CompletedKey(root), ct);
        if (completed is not null)
        {
            var result = DeserializeResult(completed.ResponseBodyJson);
            return result is null
                ? new PartnerOperationClaim(PartnerClaimKind.InFlight, null)
                : new PartnerOperationClaim(PartnerClaimKind.Replay, result);
        }

        if (await _state.GetAsync(UncertainKey(root), ct) is not null)
        {
            return new PartnerOperationClaim(PartnerClaimKind.InFlight, null);
        }

        var generation = await ResolveLatestGenerationAsync(root, ct);
        if (generation >= MaxClaimGenerations
            || await _state.GetAsync(ReleasedKey(root, generation), ct) is null)
        {
            return new PartnerOperationClaim(PartnerClaimKind.InFlight, null);
        }

        var retry = await _state.PutOrGetAsync(
            ClaimKey(root, generation + 1), 202, intentJson, TtlSeconds, ct);
        return retry.Inserted
            ? new PartnerOperationClaim(PartnerClaimKind.Won, null)
            : new PartnerOperationClaim(PartnerClaimKind.InFlight, null);
    }

    public async Task CompleteAsync(
        PartnerOperationKey key,
        Guid transactionId,
        PartnerWalletMoveResponse result,
        CancellationToken ct)
    {
        var root = RootKey(key);
        var outcome = await _state.PutOrGetAsync(
            CompletedKey(root), 200, JsonSerializer.Serialize(result, Json), TtlSeconds, ct);
        if (!outcome.Inserted)
        {
            _log.LogWarning(
                "Partner wallet completion for {Root} already exists; preserving the authoritative first result.",
                root);
        }
    }

    public async Task ReleaseAsync(PartnerOperationKey key, CancellationToken ct)
    {
        var root = RootKey(key);
        if (await _state.GetAsync(CompletedKey(root), ct) is not null
            || await _state.GetAsync(UncertainKey(root), ct) is not null)
        {
            return;
        }

        var generation = await ResolveLatestGenerationAsync(root, ct);
        await _state.PutOrGetAsync(
            ReleasedKey(root, generation), 200, "{}", TtlSeconds, ct);
    }

    public async Task MarkUncertainAsync(PartnerOperationKey key, CancellationToken ct)
    {
        var root = RootKey(key);
        if (await _state.GetAsync(CompletedKey(root), ct) is not null)
        {
            return;
        }

        await _state.PutOrGetAsync(UncertainKey(root), 202, "{}", TtlSeconds, ct);
    }

    private async Task<int> ResolveLatestGenerationAsync(string root, CancellationToken ct)
    {
        for (var generation = 2; generation <= MaxClaimGenerations; generation++)
        {
            if (await _state.GetAsync(ClaimKey(root, generation), ct) is null)
            {
                return generation - 1;
            }
        }

        return MaxClaimGenerations;
    }

    private static bool IntentMatches(string storedJson, PartnerOperationIntent presented)
    {
        try
        {
            return JsonSerializer.Deserialize<PartnerOperationIntent>(storedJson, Json) == presented;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PartnerWalletMoveResponse? DeserializeResult(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PartnerWalletMoveResponse>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RootKey(PartnerOperationKey key)
    {
        var material = $"{(int)key.Type}\n{key.ActorId:N}\n{key.IdempotencyKey}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
        return $"partner-wallet-operation:{digest}";
    }

    private static string ClaimKey(string root, int generation) => $"{root}:claim:{generation}";
    private static string ReleasedKey(string root, int generation) => $"{root}:released:{generation}";
    private static string CompletedKey(string root) => $"{root}:completed";
    private static string UncertainKey(string root) => $"{root}:uncertain";
}
