namespace ECHAT.Models.Dtos;

public class MessageAck
{
    public long Seq { get; init; }
    public DateTime AcceptedAt { get; init; }
}
