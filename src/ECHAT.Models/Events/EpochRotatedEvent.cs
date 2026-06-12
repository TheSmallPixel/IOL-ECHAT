namespace ECHAT.Models.Events;

public class EpochRotatedEvent : RealtimeEvent
{
    public int NewEpochId { get; init; }
}
