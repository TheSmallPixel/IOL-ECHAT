using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Interfaces;

public interface ILocalStore
{
    Task StoreMessageAsync(Guid conversationId, DecryptedMessage message);
    Task<List<DecryptedMessage>> GetMessagesAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit);
    Task<long?> GetLastAppliedSeqAsync(Guid conversationId);
    Task SetLastAppliedSeqAsync(Guid conversationId, long seq);
    Task ClearConversationAsync(Guid conversationId);
}
