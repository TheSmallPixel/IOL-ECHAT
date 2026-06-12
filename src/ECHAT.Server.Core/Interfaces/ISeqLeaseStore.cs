namespace ECHAT.Server.Core.Interfaces;

public interface ISeqLeaseStore
{
    Task AddAsync(SeqLeaseRecord lease);
    Task<SeqLeaseRecord?> FindByTokenAsync(string leaseToken);
    Task<int> PurgeExpiredAsync(DateTime now);
}

public class SeqLeaseRecord
{
    public string LeaseToken { get; init; } = string.Empty;
    public Guid ConversationId { get; init; }
    public Guid DeviceId { get; init; }
    public long StartSeq { get; init; }
    public long EndSeq { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
