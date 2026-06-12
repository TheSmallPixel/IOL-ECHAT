using ECHAT.Models.Dtos;
using ECHAT.Models.Events;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Moderazione dei messaggi (hide reversibile, E2EE-safe). Il permesso <c>ModerateMessages</c>
/// (Owner/Admin/Moderator) è già verificato dal filtro sul controller; qui vive la regola di
/// gerarchia ("non puoi moderare chi ha un ruolo superiore al tuo"), l'applicazione del flag,
/// l'audit e la notifica realtime.
///
/// Nota E2EE: "hide" è un flag server-side: il ciphertext NON viene toccato (la chain resta
/// intatta) e un client onesto mostra un placeholder. Non è cancellazione crittografica: chi
/// possiede la CEK e aggira la UI può ancora decifrare. La rimozione hard (redazione del
/// ciphertext) è un follow-up separato.
/// </summary>
public class MessageModerationService
{
    private readonly IMessageRepository _messages;
    private readonly IMembershipReader _membership;
    private readonly IAuditLog _audit;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<MessageModerationService> _logger;

    public MessageModerationService(
        IMessageRepository messages,
        IMembershipReader membership,
        IAuditLog audit,
        IRealtimeNotifier notifier,
        ILogger<MessageModerationService> logger)
    {
        _messages = messages;
        _membership = membership;
        _audit = audit;
        _notifier = notifier;
        _logger = logger;
    }

    // Gerarchia dei ruoli. Member ed ex-membro (ruolo null) = 0, così un mittente che ha lasciato la
    // conversazione resta moderabile da chiunque abbia il permesso.
    private static int Rank(string? role) => role switch
    {
        "Owner" => 3,
        "Admin" => 2,
        "Moderator" => 1,
        _ => 0,
    };

    /// <summary>
    /// Nasconde (<paramref name="hidden"/>=true) o ri-mostra un messaggio. Ritorna l'evento emesso.
    /// </summary>
    public async Task<MessageModeratedEvent> ModerateAsync(
        Guid conversationId, Guid moderatorUserId, long seq, bool hidden, string? reason)
    {
        var msg = await _messages.GetBySeqAsync(conversationId, seq)
            ?? throw new NotFoundException("Message not found.");

        // Regola di gerarchia: puoi moderare solo chi ha ruolo <= al tuo (Owner>Admin>Moderator>Member).
        // La moderazione del proprio messaggio è sempre consentita.
        if (msg.SenderUserId != moderatorUserId)
        {
            var moderatorRole = await _membership.GetRoleAsync(moderatorUserId, conversationId);
            var senderRole = await _membership.GetRoleAsync(msg.SenderUserId, conversationId);
            if (Rank(senderRole) > Rank(moderatorRole))
            {
                _logger.LogWarning(
                    "Moderation denied (target outranks moderator): moderator={Moderator}({MRole}) sender={Sender}({SRole}) conversation={Conversation} seq={Seq}",
                    moderatorUserId, moderatorRole, msg.SenderUserId, senderRole, conversationId, seq);
                throw new ForbiddenException();
            }
        }

        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim();
        var ok = await _messages.SetModerationAsync(conversationId, seq, hidden, moderatorUserId, trimmedReason);
        if (!ok) throw new NotFoundException("Message not found.");

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = moderatorUserId,
            Action = hidden ? "MessageHidden" : "MessageUnhidden",
            Timestamp = DateTime.UtcNow,
            Details = $"seq={seq} messageId={msg.MessageId} sender={msg.SenderUserId}"
                      + (hidden && trimmedReason is not null ? $" reason={trimmedReason}" : string.Empty),
        });

        var evt = new MessageModeratedEvent
        {
            ConversationId = conversationId,
            Timestamp = DateTime.UtcNow,
            MessageId = msg.MessageId,
            Seq = seq,
            Hidden = hidden,
            ByUserId = moderatorUserId,
        };
        await _notifier.NotifyAsync(conversationId, evt);

        _logger.LogInformation(
            "Message {Action}: conversation={Conversation} seq={Seq} by={Moderator}",
            hidden ? "hidden" : "unhidden", conversationId, seq, moderatorUserId);

        return evt;
    }
}
