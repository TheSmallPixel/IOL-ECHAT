using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Orchestrazione della risposta automatica AI per un nuovo messaggio: ritardo casuale, fetch del
/// contesto, skip se l'ultimo messaggio è nostro, generazione e validazione del reply prima
/// dell'invio. L'event wiring (SignalR), lo stato di abilitazione e il logging restano nell'App.
/// </summary>
public interface IAiAutoReplyOrchestrator
{
    /// <summary>
    /// Esegue il flusso di reply per la conversazione. <paramref name="fetchContext"/> recupera i
    /// messaggi recenti; <paramref name="sendReply"/> invia il testo ripulito. Non invia nulla se la
    /// cancellazione scatta, se l'ultimo messaggio visibile è di <paramref name="myUserId"/>, o se il
    /// reply (dopo cleanup) è vuoto/whitespace.
    /// </summary>
    Task RunAsync(
        Guid myUserId,
        IReadOnlyDictionary<Guid, string> memberNames,
        Func<CancellationToken, Task<List<DecryptedMessage>>> fetchContext,
        Func<string, CancellationToken, Task> sendReply,
        CancellationToken ct);
}
