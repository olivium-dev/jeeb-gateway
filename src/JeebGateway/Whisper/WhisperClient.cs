using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace JeebGateway.Whisper;

/// <summary>
/// HttpClient wrapper around OpenAI's <c>POST /v1/audio/transcriptions</c>.
/// Translates transient failure modes (timeout, network, 5xx, 429) into
/// <see cref="WhisperUnavailableException"/>; auth/validation errors propagate.
/// Per-attempt timeout is enforced by the caller via cancellation token.
/// </summary>
public sealed class WhisperClient : IWhisperClient
{
    private readonly HttpClient _http;
    private readonly WhisperOptions _options;

    public WhisperClient(HttpClient http, IOptions<WhisperOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<WhisperTranscription> TranscribeAsync(WhisperAudio audio, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio.Content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(audio.ContentType);
        content.Add(fileContent, "file", audio.FileName);
        content.Add(new StringContent(_options.Model), "model");
        content.Add(new StringContent(_options.Language), "language");
        content.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions") { Content = content };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient surfaces per-attempt timeout as OperationCanceledException when the
            // external token is not yet cancelled.
            throw new WhisperUnavailableException("whisper request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new WhisperUnavailableException("whisper transport failure", ex);
        }

        using var _ = response;

        if (IsTransient(response.StatusCode))
        {
            throw new WhisperUnavailableException(
                $"whisper returned transient status {(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<WhisperApiResponse>(cancellationToken: ct)
                   ?? throw new WhisperUnavailableException("whisper returned empty body");

        return new WhisperTranscription(body.Text ?? string.Empty, _options.Language);
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout;

    private sealed record WhisperApiResponse(string? Text);
}
