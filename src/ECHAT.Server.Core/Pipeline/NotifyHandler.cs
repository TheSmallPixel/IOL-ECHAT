using ECHAT.Models.Events;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ECHAT.Server.Core.Pipeline;

public class NotifyHandler : IIngestHandler
{
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<NotifyHandler> _logger;

    public NotifyHandler(IRealtimeNotifier notifier, ILogger<NotifyHandler>? logger = null)
    {
        _notifier = notifier;
        _logger = logger ?? NullLogger<NotifyHandler>.Instance;
    }

    public async Task HandleAsync(IngestContext context, Func<Task> next)
    {
        // La notifica realtime è un side-effect: gira DOPO PersistHandler (il messaggio è già nello
        // store). Un fallimento (SignalR giù, ecc.) NON deve trasformare un ingest riuscito in un 500:
        // il client crederebbe fallito e ritenterebbe. Logghiamo e proseguiamo (best-effort).
        try
        {
            await _notifier.NotifyAsync(context.Envelope.ConversationId, new MessageAvailableEvent
            {
                ConversationId = context.Envelope.ConversationId,
                Seq = context.Envelope.Seq,
                MessageId = context.Envelope.MessageId,
                EpochId = context.Envelope.EpochId,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Realtime notify failed (message already persisted): conversation={ConversationId} seq={Seq}",
                context.Envelope.ConversationId, context.Envelope.Seq);
        }

        await next();
    }
}
