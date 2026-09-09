using System.Net;
using System.Text;
using System.Text.Json;
using JeebGateway.Chat.Firebase;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class ChatFirebaseIdentityClientTests
{
    [Fact]
    public async Task Uses_private_owner_route_and_preserves_the_owner_wire_identity()
    {
        var handler = new OwnerHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://chat-owner.test/") };
        var response = await new ChatFirebaseIdentityClient(http).MintAsync("opaque-actor", CancellationToken.None);
        Assert.Equal("/api/firebase/token", handler.Path);
        Assert.Equal("opaque-actor", handler.Uid);
        Assert.Equal("opaque-actor", response.Uid);
        Assert.Equal("synthetic-owner-token", response.Token);
        Assert.Equal(1800, response.ExpiresInSeconds);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Missing_or_unavailable_owner_is_not_a_local_mint_fallback(HttpStatusCode status)
    {
        using var http = new HttpClient(new OwnerHandler { Status = status })
            { BaseAddress = new Uri("http://chat-owner.test/") };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ChatFirebaseIdentityClient(http).MintAsync("actor", CancellationToken.None));
    }

    [Fact]
    public void Gateway_assembly_has_no_firebase_signer_or_signer_options()
    {
        Assert.Null(typeof(Program).Assembly.GetType("JeebGateway.Chat.Firebase.FirebaseCustomTokenMinter"));
        Assert.Null(typeof(Program).Assembly.GetType("JeebGateway.Chat.Firebase.FirebaseCustomTokenOptions"));
    }

    private sealed class OwnerHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? Path { get; private set; }
        public string? Uid { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Headers.Authorization); // Existing isolated-owner contract, not an invented service bearer.
            Path = request.RequestUri!.AbsolutePath;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Single(body.RootElement.EnumerateObject());
            Uid = body.RootElement.GetProperty("uid").GetString();
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    token = "synthetic-owner-token", uid = Uid,
                    expires_at = DateTime.UtcNow.AddMinutes(30), expires_in_seconds = 1800,
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
