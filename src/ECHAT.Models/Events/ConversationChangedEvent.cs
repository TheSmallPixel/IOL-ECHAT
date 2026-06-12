namespace ECHAT.Models.Events;

/// <summary>
/// Notifica un cambiamento a livello di conversazione (non di membership): rinomina o cancellazione.
/// La UI client aggiorna la sidebar di conseguenza: rinomina l'elemento o lo rimuove.
/// </summary>
public class ConversationChangedEvent : RealtimeEvent
{
    /// <summary>"Renamed" oppure "Deleted".</summary>
    public string ChangeType { get; init; } = string.Empty;

    /// <summary>Nuovo nome (valorizzato solo per <c>ChangeType == "Renamed"</c>).</summary>
    public string? Name { get; init; }
}
