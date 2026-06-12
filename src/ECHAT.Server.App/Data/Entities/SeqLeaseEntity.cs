namespace ECHAT.Server.App.Data.Entities;

public class SeqLeaseEntity
{
    public long Id { get; set; }
    public string LeaseToken { get; set; } = string.Empty;
    public Guid ConversationId { get; set; }
    public Guid DeviceId { get; set; }
    public long StartSeq { get; set; }
    public long EndSeq { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
