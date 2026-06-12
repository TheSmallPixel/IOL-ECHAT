using ECHAT.Models.Events;

namespace ECHAT.Server.Core.Interfaces;

public interface IRealtimeNotifier
{
    /// <summary>
    /// Notifica tutti i membri *attivi* correnti della conversazione. L'implementazione risolve
    /// la lista dei destinatari al momento dell'invio, quindi un utente rimosso non riceve
    /// l'evento anche se ha ancora una connessione SignalR aperta.
    /// </summary>
    Task NotifyAsync(Guid conversationId, RealtimeEvent evt);

    /// <summary>
    /// Notifica un set esplicito di utenti: usato quando l'audience non coincide con
    /// "membri attivi correnti" (es. evento <c>MemberChanged/Removed</c>: vogliamo includere
    /// anche l'utente appena rimosso così la sua UI nasconde la conversazione).
    /// </summary>
    Task NotifyUsersAsync(IEnumerable<Guid> userIds, RealtimeEvent evt);
}
