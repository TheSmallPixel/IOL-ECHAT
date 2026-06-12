namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Sorgente unica per lo stato delle migrazioni in corso, condivisa tra ChatSdkService (per
/// bloccare l'invio) e la UI (per il banner). Si alimenta da due lati:
///   - <see cref="BeginLocal"/> + <see cref="Update"/> da chi guida la saga su questo device
///     (es. CustodianWorker invoked da ChatSdkService).
///   - SignalR <c>JobProgressEvent</c> per conversazioni guidate da altri device/admin:
///     l'implementazione si sottoscrive al realtime client e popola lo stato in entrata.
///
/// Quando un'operazione è guidata localmente, gli eventi SignalR per la stessa conversazione
/// sono ignorati (il flusso IProgress locale è più preciso, evita doppio update).
/// </summary>
public interface IMigrationStateTracker
{
    /// <summary>Stato corrente per la conversazione, o null se non c'è una migrazione attiva.</summary>
    MigrationProgress? Get(Guid conversationId);

    /// <summary>Shortcut: c'è una migrazione attiva (qualsiasi fase non-terminale)?</summary>
    bool IsActive(Guid conversationId);

    /// <summary>
    /// Marca la conversazione come "guidata da questo device". Restituisce un <see cref="IDisposable"/>
    /// che ripulisce lo stato quando viene disposto. Tipico uso:
    /// <code>using var scope = tracker.BeginLocal(convId);</code>
    /// </summary>
    IDisposable BeginLocal(Guid conversationId);

    /// <summary>
    /// Aggiorna lo stato per una conversazione. Usato dal callback IProgress locale; gli eventi
    /// SignalR vengono assorbiti internamente.
    /// </summary>
    void Update(Guid conversationId, MigrationProgress progress);

    /// <summary>
    /// Notifica del cambio di stato per la conversazione indicata. La UI lo usa per
    /// re-renderizzare il banner / disabilitare il composer.
    /// </summary>
    event Action<Guid>? StateChanged;
}
