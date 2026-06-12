namespace ECHAT.Models.Events;

public abstract class RealtimeEvent
{
    public Guid ConversationId { get; init; }
    public DateTime Timestamp { get; init; }
}
