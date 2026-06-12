using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Pipeline;

public class DeduplicationHandler : IIngestHandler
{
    private readonly IMessageRepository _messageRepository;

    public DeduplicationHandler(IMessageRepository messageRepository)
    {
        _messageRepository = messageRepository;
    }

    public async Task HandleAsync(IngestContext context, Func<Task> next)
    {
        var exists = await _messageRepository.ExistsAsync(context.Envelope.MessageId);
        if (exists)
        {
            context.IsDuplicate = true;
            return;
        }

        await next();
    }
}
