using System.Net;
using System.Net.Http;

namespace ECHAT.Server.Core.Interfaces;

/// <summary>
/// Opzioni per la generazione della reply AI. Lette dalla configurazione nella App e passate qui,
/// così Core non dipende da <c>IConfiguration</c>.
/// </summary>
public class AiReplyOptions
{
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "gpt-4o-mini";
}

/// <summary>
/// Sollevata quando OpenAI risponde con uno status non di successo. Trasporta lo status code e il
/// corpo della risposta così il controller può riprodurre la mappatura HTTP originale.
/// </summary>
public class AiUpstreamException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string Body { get; }

    public AiUpstreamException(HttpStatusCode statusCode, string body) : base(body)
    {
        StatusCode = statusCode;
        Body = body;
    }
}

/// <summary>
/// Integrazione con OpenAI per generare una reply: validazione API key, costruzione del payload
/// (system prompt + temperature/penalty), POST a chat/completions, parsing della risposta e gestione
/// degli errori. La App fornisce l'<see cref="HttpClient"/> (via IHttpClientFactory) e le opzioni;
/// il controller mantiene endpoint/[Authorize]/DTO e la mappatura eccezione->HTTP.
/// </summary>
public interface IAiReplyService
{
    /// <summary>
    /// Ritorna la reply generata (eventualmente null/empty se l'upstream non produce contenuto).
    /// Lancia <see cref="InvalidOperationException"/> se l'API key non è configurata e
    /// <see cref="AiUpstreamException"/> se OpenAI risponde con errore.
    /// </summary>
    Task<string?> GetAiReplyAsync(HttpClient client, AiReplyOptions options, string prompt);
}
