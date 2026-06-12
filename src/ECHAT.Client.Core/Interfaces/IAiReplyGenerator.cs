namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Genera una risposta automatica dal prompt. L'implementazione concreta sta in Client.App
/// e chiama l'endpoint server lato OpenAI; qui resta solo il contratto.
/// </summary>
public interface IAiReplyGenerator
{
    Task<string?> GenerateReplyAsync(string prompt, CancellationToken ct);
}
