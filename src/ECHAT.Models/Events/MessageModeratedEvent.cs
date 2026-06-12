namespace ECHAT.Models.Events;

/// <summary>
/// Un messaggio è stato nascosto o ri-mostrato da un moderatore/admin. I client aggiornano in
/// place lo stato del messaggio (placeholder ↔ contenuto) senza un full reload.
/// </summary>
public class MessageModeratedEvent : RealtimeEvent
{
    public Guid MessageId { get; init; }
    public long Seq { get; init; }
    /// <summary>true = nascosto, false = ri-mostrato.</summary>
    public bool Hidden { get; init; }
    /// <summary>Utente (moderatore/admin) che ha eseguito l'azione.</summary>
    public Guid ByUserId { get; init; }
}
