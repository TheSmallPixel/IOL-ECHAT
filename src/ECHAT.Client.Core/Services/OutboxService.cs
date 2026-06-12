using ECHAT.Client.Core.Commands;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Services;

public class OutboxService : IOutbox
{
    private readonly IOutboxStore _store;
    private readonly object _lock = new();

    public OutboxService() : this(new InMemoryOutboxStore()) { }

    public OutboxService(IOutboxStore store)
    {
        _store = store;
    }

    public Task EnqueueAsync(SendMessageCommand command)
    {
        lock (_lock)
        {
            var items = _store.Load().ToList();
            items.Add(new OutboxItem
            {
                MessageId = command.MessageId,
                ConversationId = command.ConversationId,
                Payload = command.Payload,
                State = OutboxState.Pending,
                RetryCount = 0
            });
            _store.Save(items);
        }
        return Task.CompletedTask;
    }

    public Task<List<OutboxItem>> GetPendingAsync()
    {
        lock (_lock)
        {
            var pending = _store.Load()
                .Where(i => i.State == OutboxState.Pending || i.State == OutboxState.Failed)
                .ToList();
            return Task.FromResult(pending);
        }
    }

    public Task AckAsync(Guid messageId)
    {
        lock (_lock)
        {
            var items = _store.Load().ToList();
            var item = items.FirstOrDefault(i => i.MessageId == messageId);
            if (item != null)
            {
                item.State = OutboxState.Acked;
                _store.Save(items);
            }
        }
        return Task.CompletedTask;
    }

    public Task FailAsync(Guid messageId, string reason)
    {
        lock (_lock)
        {
            var items = _store.Load().ToList();
            var item = items.FirstOrDefault(i => i.MessageId == messageId);
            if (item != null)
            {
                item.State = OutboxState.Failed;
                item.FailureReason = reason;
                item.RetryCount++;
                _store.Save(items);
            }
        }
        return Task.CompletedTask;
    }
}
