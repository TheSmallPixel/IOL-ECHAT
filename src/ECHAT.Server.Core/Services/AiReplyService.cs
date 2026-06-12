using System.Net.Http.Json;
using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <inheritdoc cref="IAiReplyService"/>
public class AiReplyService : IAiReplyService
{
    private const string SystemPrompt =
        "You are a creative and fun chat participant. Reply briefly (1-3 sentences). Be imaginative: invent stories, share fake fun facts, ask wild questions, change topics when things get boring. Match the language of the conversation. Never be repetitive.";

    public async Task<string?> GetAiReplyAsync(HttpClient client, AiReplyOptions options, string prompt)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
            throw new InvalidOperationException("OpenAI API key not configured.");

        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");

        var body = new
        {
            model = options.Model,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt }
            },
            max_tokens = 200,
            temperature = 1.1,
            presence_penalty = 0.6,
            frequency_penalty = 0.5
        };

        var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", body);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new AiUpstreamException(response.StatusCode, error);
        }

        var result = await response.Content.ReadFromJsonAsync<OpenAiResponse>();
        return result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
    }
}

public class OpenAiResponse
{
    public List<OpenAiChoice>? Choices { get; set; }
}

public class OpenAiChoice
{
    public OpenAiMessage? Message { get; set; }
}

public class OpenAiMessage
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}
