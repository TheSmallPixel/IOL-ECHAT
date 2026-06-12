using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ECHAT.Server.Core.Services;

public class MessageIngestPipeline : IMessageIngestPipeline
{
    private readonly IReadOnlyList<IIngestHandler> _handlers;
    private readonly ILogger<MessageIngestPipeline> _logger;

    public MessageIngestPipeline(IEnumerable<IIngestHandler> handlers)
        : this(handlers, NullLogger<MessageIngestPipeline>.Instance) { }

    public MessageIngestPipeline(IEnumerable<IIngestHandler> handlers, ILogger<MessageIngestPipeline> logger)
    {
        _handlers = handlers.ToList().AsReadOnly();
        _logger = logger;
    }

    public async Task<MessageAck> IngestAsync(MessageEnvelope envelope, Guid userId)
    {
        var context = new IngestContext(envelope) { UserId = userId };

        async Task ExecuteChain(int index)
        {
            if (index < _handlers.Count)
            {
                await _handlers[index].HandleAsync(context, () => ExecuteChain(index + 1));
            }
        }

        try
        {
            await ExecuteChain(0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Ingest rejected: conversation={ConversationId} seq={Seq} messageId={MessageId} reason={Reason}",
                envelope.ConversationId, envelope.Seq, envelope.MessageId, ex.Message);
            throw;
        }

        if (context.IsDuplicate)
        {
            _logger.LogInformation(
                "Ingest duplicate: conversation={ConversationId} seq={Seq} messageId={MessageId}",
                envelope.ConversationId, envelope.Seq, envelope.MessageId);
            return new MessageAck
            {
                Seq = envelope.Seq,
                AcceptedAt = DateTime.UtcNow
            };
        }

        _logger.LogDebug(
            "Ingest accepted: conversation={ConversationId} seq={Seq} messageId={MessageId}",
            envelope.ConversationId, envelope.Seq, envelope.MessageId);

        return context.Ack ?? new MessageAck
        {
            Seq = envelope.Seq,
            AcceptedAt = DateTime.UtcNow
        };
    }
}
