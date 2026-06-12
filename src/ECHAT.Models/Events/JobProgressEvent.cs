namespace ECHAT.Models.Events;

public class JobProgressEvent : RealtimeEvent
{
    public Guid JobId { get; init; }
    public int ProgressPercent { get; init; }
    public string Status { get; init; } = string.Empty;
}
