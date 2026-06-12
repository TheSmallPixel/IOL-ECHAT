using System.Net.Http.Json;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Adapter HTTP che chiama l'endpoint server <c>/api/ai/reply</c> (proxy verso OpenAI).
/// </summary>
public class HttpAiReplyGenerator : IAiReplyGenerator
{
    private readonly HttpClient _http;
    private readonly TokenAuthStateProvider _authState;

    public HttpAiReplyGenerator(HttpClient http, TokenAuthStateProvider authState)
    {
        _http = http;
        _authState = authState;
    }

    public async Task<string?> GenerateReplyAsync(string prompt, CancellationToken ct)
    {
        var token = await _authState.GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _http.PostAsJsonAsync("api/ai/reply", new { prompt }, ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadFromJsonAsync<AiReplyResponse>(ct);
        return result?.Reply;
    }

    private class AiReplyResponse
    {
        public string? Reply { get; set; }
    }
}
