using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Interfaces;

public interface IScrollLoader
{
    Task<List<DecryptedMessage>> LoadMoreAsync(Guid conversationId, long beforeSeq, int limit);
    Task<List<DecryptedMessage>> LoadRecentAsync(Guid conversationId, long afterSeq, int limit);
}
