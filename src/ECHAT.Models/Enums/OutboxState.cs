namespace ECHAT.Models.Enums;

public enum OutboxState
{
    Pending,
    Sending,
    Sent,
    Acked,
    Failed
}
