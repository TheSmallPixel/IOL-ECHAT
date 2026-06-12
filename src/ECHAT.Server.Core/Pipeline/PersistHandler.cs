using ECHAT.Models.Domain;
using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Pipeline;

public class PersistHandler : IIngestHandler
{
    private readonly IMessageRepository _messageRepository;
    private readonly ISeqCounterStore _counterStore;

    public PersistHandler(IMessageRepository messageRepository, ISeqCounterStore counterStore)
    {
        _messageRepository = messageRepository;
        _counterStore = counterStore;
    }

    public async Task HandleAsync(IngestContext context, Func<Task> next)
    {
        await _messageRepository.AppendAsync(context.Envelope);

        var hash = EnvelopeHasher.Compute(context.Envelope);
        await _counterStore.UpdateAnchorAsync(
            context.Envelope.ConversationId,
            context.Envelope.Seq,
            hash);

        await next();
    }
}
