using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Logica pura della risposta automatica AI (vedi <see cref="IAiAutoReplyOrchestrator"/>).
/// Il prompt e il cleanup vivono in <see cref="AiReplyComposer"/>; qui c'è il flusso: ritardo
/// casuale, fetch del contesto, skip self-reply, validazione del reply prima dell'invio.
/// </summary>
public sealed class AiAutoReplyOrchestrator : IAiAutoReplyOrchestrator
{
    /// <summary>Ritardo minimo (incluso) prima di rispondere, in ms.</summary>
    public const int MinDelayMs = 1000;

    /// <summary>Ritardo massimo (escluso) prima di rispondere, in ms.</summary>
    public const int MaxDelayMs = 4000;

    /// <summary>Quanti messaggi recenti recuperare come contesto.</summary>
    public const int ContextFetchLimit = 20;

    private readonly IAiReplyGenerator _ai;
    private readonly IDelayProvider _delay;

    public AiAutoReplyOrchestrator(IAiReplyGenerator ai, IDelayProvider delay)
    {
        _ai = ai;
        _delay = delay;
    }

    public async Task RunAsync(
        Guid myUserId,
        IReadOnlyDictionary<Guid, string> memberNames,
        Func<CancellationToken, Task<List<DecryptedMessage>>> fetchContext,
        Func<string, CancellationToken, Task> sendReply,
        CancellationToken ct)
    {
        // Ritardo casuale 1-4 secondi (simula la digitazione)
        await _delay.DelayRandomAsync(MinDelayMs, MaxDelayMs, ct);

        if (ct.IsCancellationRequested) return;

        // Recupera i messaggi recenti come contesto
        var messages = await fetchContext(ct);

        // Salta se l'ultimo messaggio è mio
        var lastMsg = messages.LastOrDefault(m => !m.Invisible);
        if (lastMsg == null || lastMsg.SenderDeviceId == myUserId) return;

        var prompt = AiReplyComposer.BuildContext(messages, myUserId, memberNames);
        var reply = await _ai.GenerateReplyAsync(prompt, ct);

        if (!string.IsNullOrWhiteSpace(reply) && !ct.IsCancellationRequested)
        {
            var cleaned = AiReplyComposer.CleanReply(reply, myUserId, memberNames);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                await sendReply(cleaned, ct);
            }
        }
    }
}
