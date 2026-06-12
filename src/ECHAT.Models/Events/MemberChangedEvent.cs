namespace ECHAT.Models.Events;

public class MemberChangedEvent : RealtimeEvent
{
    public Guid UserId { get; init; }
    /// <summary>"Added" oppure "Removed".</summary>
    public string Action { get; init; } = string.Empty;
}
