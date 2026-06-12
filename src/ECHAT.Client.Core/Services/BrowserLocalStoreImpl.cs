using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Cache dei messaggi decifrati su un key/value store (<see cref="ILocalStorageTransport"/>).
/// Chiave per conversazione: echat_msgs:{conversationId}. Ultimo seq: echat_seq:{conversationId}.
/// La logica (dedup, sort per seq, range filter, limit) è qui; il transport gestisce solo l'I/O.
/// </summary>
public class BrowserLocalStoreImpl : ILocalStore
{
    private const string MsgsPrefix = "echat_msgs:";
    private const string SeqPrefix = "echat_seq:";

    private readonly ILocalStorageTransport _storage;

    public BrowserLocalStoreImpl(ILocalStorageTransport storage)
    {
        _storage = storage;
    }

    private static string MsgsKey(Guid conversationId) => $"{MsgsPrefix}{conversationId}";
    private static string SeqKey(Guid conversationId) => $"{SeqPrefix}{conversationId}";

    public async Task StoreMessageAsync(Guid conversationId, DecryptedMessage message)
    {
        var list = await _storage.GetAsync<List<DecryptedMessage>>(MsgsKey(conversationId)) ?? new();
        if (list.All(m => m.MessageId != message.MessageId))
        {
            list.Add(message);
            list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            await _storage.SetAsync(MsgsKey(conversationId), list);
        }
    }

    public async Task<List<DecryptedMessage>> GetMessagesAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        var list = await _storage.GetAsync<List<DecryptedMessage>>(MsgsKey(conversationId)) ?? new();
        IEnumerable<DecryptedMessage> q = list;
        if (afterSeq.HasValue) q = q.Where(m => m.Seq > afterSeq.Value);
        if (beforeSeq.HasValue) q = q.Where(m => m.Seq < beforeSeq.Value);
        return q.Take(limit).ToList();
    }

    public Task<long?> GetLastAppliedSeqAsync(Guid conversationId)
        => _storage.GetAsync<long?>(SeqKey(conversationId));

    public Task SetLastAppliedSeqAsync(Guid conversationId, long seq)
        => _storage.SetAsync<long?>(SeqKey(conversationId), seq);

    public async Task ClearConversationAsync(Guid conversationId)
    {
        await _storage.RemoveAsync(MsgsKey(conversationId));
        await _storage.RemoveAsync(SeqKey(conversationId));
    }
}
