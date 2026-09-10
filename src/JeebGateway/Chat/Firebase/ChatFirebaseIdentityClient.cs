using System.Net.Http.Json;
using JeebGateway.Controllers;

namespace JeebGateway.Chat.Firebase;

public interface IChatFirebaseIdentityClient
{
    Task<FirebaseTokenResponse> MintAsync(string uid, CancellationToken ct);
}

/// <summary>Private-network owner client, matching JeebConversationClient's trust
/// boundary. No gateway Firebase credentials, local signer, or fallback.</summary>
public sealed class ChatFirebaseIdentityClient(HttpClient http) : IChatFirebaseIdentityClient
{
    public async Task<FirebaseTokenResponse> MintAsync(string uid, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/firebase/token", new { uid }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FirebaseTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Chat owner returned no identity.");
    }
}
