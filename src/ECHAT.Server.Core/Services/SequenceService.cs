using System.Collections.Concurrent;
using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ECHAT.Server.Core.Services;

public class SequenceService : ISequenceService
{
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(10);

    // Lock per-conversazione: ReserveRangeAsync legge NextSeq dal DB, lo incrementa e lo
    // salva. Senza serializzazione, due chiamate concorrenti per la stessa conversazione
    // possono leggere lo stesso NextSeq  assegnare lo stesso range a due lease diversi 
    // collisione lato persist o chain break. Semaforo in-process è sufficiente per un
    // singolo nodo; per multi-instance servirebbe un lock distribuito (Redis, advisory locks,
    // o un'UPDATE atomica con FOR UPDATE).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ReserveLocks = new();

    private readonly ISeqCounterStore _counters;
    private readonly ISeqLeaseStore _leases;
    private readonly IMessageRepository _messages;
    private readonly ILogger<SequenceService> _logger;

    public SequenceService(
        ISeqCounterStore counters,
        ISeqLeaseStore leases,
        IMessageRepository messages,
        ILogger<SequenceService>? logger = null)
    {
        _counters = counters;
        _leases = leases;
        _messages = messages;
        _logger = logger ?? NullLogger<SequenceService>.Instance;
    }

    public async Task<SeqReservation> ReserveRangeAsync(Guid conversationId, Guid deviceId, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

        var sem = ReserveLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            var maxMessageSeq = await _messages.GetMaxSeqAsync(conversationId);
            var range = await _counters.ReserveRangeAsync(conversationId, count, maxMessageSeq);

            var leaseToken = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;

            await _leases.AddAsync(new SeqLeaseRecord
            {
                LeaseToken = leaseToken,
                ConversationId = conversationId,
                DeviceId = deviceId,
                StartSeq = range.StartSeq,
                EndSeq = range.EndSeq,
                IssuedAt = now,
                ExpiresAt = now.Add(LeaseTtl)
            });

            _logger.LogInformation(
                "Seq range reserved: conversationId={ConversationId} deviceId={DeviceId} count={Count} startSeq={StartSeq} endSeq={EndSeq}",
                conversationId, deviceId, count, range.StartSeq, range.EndSeq);

            return new SeqReservation
            {
                StartSeq = range.StartSeq,
                EndSeq = range.EndSeq,
                LeaseToken = leaseToken,
                AnchorSeq = range.AnchorSeq,
                AnchorEnvelopeHash = range.AnchorEnvelopeHash
            };
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<bool> ValidateLeaseAsync(Guid conversationId, long seq, string leaseToken)
    {
        if (string.IsNullOrEmpty(leaseToken))
        {
            _logger.LogWarning(
                "Seq lease validation failed: conversationId={ConversationId} seq={Seq} reason=empty-token",
                conversationId, seq);
            return false;
        }

        var lease = await _leases.FindByTokenAsync(leaseToken);
        if (lease is null)
        {
            _logger.LogWarning(
                "Seq lease validation failed: conversationId={ConversationId} seq={Seq} reason=unknown-token",
                conversationId, seq);
            return false;
        }
        if (lease.ConversationId != conversationId)
        {
            _logger.LogWarning(
                "Seq lease validation failed: conversationId={ConversationId} seq={Seq} reason=conversation-mismatch leaseConversation={LeaseConversationId}",
                conversationId, seq, lease.ConversationId);
            return false;
        }
        if (lease.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Seq lease validation failed: conversationId={ConversationId} seq={Seq} reason=expired expiresAt={ExpiresAt}",
                conversationId, seq, lease.ExpiresAt);
            return false;
        }
        if (seq < lease.StartSeq || seq > lease.EndSeq)
        {
            _logger.LogWarning(
                "Seq lease validation failed: conversationId={ConversationId} seq={Seq} reason=out-of-range leaseRange={StartSeq}-{EndSeq}",
                conversationId, seq, lease.StartSeq, lease.EndSeq);
            return false;
        }
        return true;
    }
}
