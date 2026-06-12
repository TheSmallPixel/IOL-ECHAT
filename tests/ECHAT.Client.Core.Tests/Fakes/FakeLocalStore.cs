using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Tests.Fakes;

public class FakeLocalStore : ILocalStore
{
    public Dictionary<Guid, List<DecryptedMessage>> Stored { get; } = new();
    public Dictionary<Guid, long> LastApplied { get; } = new();

    public Task StoreMessageAsync(Guid conversationId, DecryptedMessage message)
    {
        if (!Stored.TryGetValue(conversationId, out var list))
            Stored[conversationId] = list = new();
        list.Add(message);
        return Task.CompletedTask;
    }

    public Task<List<DecryptedMessage>> GetMessagesAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        if (!Stored.TryGetValue(conversationId, out var list))
            return Task.FromResult(new List<DecryptedMessage>());

        IEnumerable<DecryptedMessage> q = list.OrderBy(m => m.Seq);
        if (afterSeq.HasValue) q = q.Where(m => m.Seq > afterSeq.Value);
        if (beforeSeq.HasValue) q = q.Where(m => m.Seq < beforeSeq.Value);
        return Task.FromResult(q.Take(limit).ToList());
    }

    public Task<long?> GetLastAppliedSeqAsync(Guid conversationId)
        => Task.FromResult(LastApplied.TryGetValue(conversationId, out var s) ? (long?)s : null);

    public Task SetLastAppliedSeqAsync(Guid conversationId, long seq)
    {
        LastApplied[conversationId] = seq;
        return Task.CompletedTask;
    }

    public Task ClearConversationAsync(Guid conversationId)
    {
        Stored.Remove(conversationId);
        LastApplied.Remove(conversationId);
        return Task.CompletedTask;
    }
}
