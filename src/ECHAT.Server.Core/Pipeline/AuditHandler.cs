using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ECHAT.Server.Core.Pipeline;

/// <summary>
/// Registra un audit entry per ogni messaggio ingerito con successo.
/// Sta in fondo alla pipeline così duplicati e messaggi rifiutati non fanno rumore.
/// </summary>
public class AuditHandler : IIngestHandler
{
    private readonly IAuditLog _audit;
    private readonly ILogger<AuditHandler> _logger;

    public AuditHandler(IAuditLog audit, ILogger<AuditHandler>? logger = null)
    {
        _audit = audit;
        _logger = logger ?? NullLogger<AuditHandler>.Instance;
    }

    public async Task HandleAsync(IngestContext context, Func<Task> next)
    {
        // Side-effect post-persistenza: un fallimento dell'audit NON deve restituire 500 su un
        // messaggio già accettato/persistito. Best-effort: logga e prosegui.
        try
        {
            await _audit.RecordAsync(new AuditEntry
            {
                ConversationId = context.Envelope.ConversationId,
                UserId = context.UserId == Guid.Empty ? null : context.UserId,
                Action = "MessageIngested",
                Timestamp = DateTime.UtcNow,
                Details = $"seq={context.Envelope.Seq};messageId={context.Envelope.MessageId};epoch={context.Envelope.EpochId}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Audit record failed (message already persisted): conversation={ConversationId} seq={Seq}",
                context.Envelope.ConversationId, context.Envelope.Seq);
        }

        await next();
    }
}
