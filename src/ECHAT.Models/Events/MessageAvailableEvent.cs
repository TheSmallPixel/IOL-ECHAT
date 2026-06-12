namespace ECHAT.Models.Events;

public class MessageAvailableEvent : RealtimeEvent
{
    public long Seq { get; init; }
    public Guid MessageId { get; init; }
    public int EpochId { get; init; }
}
