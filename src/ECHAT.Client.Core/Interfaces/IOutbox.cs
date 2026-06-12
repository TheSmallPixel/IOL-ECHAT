using ECHAT.Client.Core.Commands;

namespace ECHAT.Client.Core.Interfaces;

public interface IOutbox
{
    Task EnqueueAsync(SendMessageCommand command);
    Task<List<OutboxItem>> GetPendingAsync();
    Task AckAsync(Guid messageId);
    Task FailAsync(Guid messageId, string reason);
}
