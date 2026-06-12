using ECHAT.Models.Domain;
using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Integration.Tests.Fakes;

public class InMemoryMessageRepository : IMessageRepository
{
    private readonly List<MessageEnvelope> _messages = new();
    private readonly object _lock = new();

    public Task AppendAsync(MessageEnvelope envelope)
    {
        lock (_lock)
        {
            if (_messages.Any(m => m.MessageId == envelope.MessageId))
                throw new InvalidOperationException($"Duplicate messageId {envelope.MessageId}");
            _messages.Add(envelope);
        }
        return Task.CompletedTask;
    }

    public Task<List<MessageEnvelope>> QueryAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        lock (_lock)
        {
            var query = _messages.Where(m => m.ConversationId == conversationId);
            if (afterSeq.HasValue)
            {
                // Live-tail in avanti: dal più vecchio, fino a `limit` messaggi con seq > afterSeq.
                return Task.FromResult(
                    query.Where(m => m.Seq > afterSeq.Value)
                        .OrderBy(m => m.Seq)
                        .Take(limit)
                        .ToList());
            }

            // Default e beforeSeq: ultimi `limit` messaggi (newest-first), poi invertiti per la visualizzazione.
            var newest = beforeSeq.HasValue
                ? query.Where(m => m.Seq < beforeSeq.Value)
                : query;
            var latest = newest.OrderByDescending(m => m.Seq).Take(limit).ToList();
            latest.Reverse();
            return Task.FromResult(latest);
        }
    }

    public Task<List<MessageEnvelope>> QueryLatestAsync(Guid conversationId, int count)
    {
        lock (_lock)
        {
            var latest = _messages
                .Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.Seq)
                .Take(count)
                .ToList();
            return Task.FromResult(latest);
        }
    }

    public Task<bool> ExistsAsync(Guid messageId)
    {
        lock (_lock)
        {
            return Task.FromResult(_messages.Any(m => m.MessageId == messageId));
        }
    }

    public Task<bool> HasMessageFromDeviceAsync(Guid conversationId, Guid senderDeviceId)
    {
        lock (_lock)
        {
            return Task.FromResult(_messages.Any(m => m.ConversationId == conversationId && m.SenderDeviceId == senderDeviceId));
        }
    }

    public Task ReplaceAsync(long seq, MessageEnvelope newEnvelope)
    {
        lock (_lock)
        {
            var idx = _messages.FindIndex(m => m.ConversationId == newEnvelope.ConversationId && m.Seq == seq);
            if (idx < 0) throw new InvalidOperationException($"Message with seq {seq} not found");
            _messages[idx] = newEnvelope;
        }
        return Task.CompletedTask;
    }

    public Task<MessageEnvelope?> GetBySeqAsync(Guid conversationId, long seq)
    {
        lock (_lock)
            return Task.FromResult(_messages.FirstOrDefault(m => m.ConversationId == conversationId && m.Seq == seq));
    }

    public Task<bool> SetModerationAsync(Guid conversationId, long seq, bool hidden, Guid moderatorUserId, string? reason)
    {
        lock (_lock)
        {
            var idx = _messages.FindIndex(m => m.ConversationId == conversationId && m.Seq == seq);
            if (idx < 0) return Task.FromResult(false);
            var m = _messages[idx];
            _messages[idx] = new MessageEnvelope
            {
                ConversationId = m.ConversationId, MessageId = m.MessageId, Seq = m.Seq, EpochId = m.EpochId,
                SenderDeviceId = m.SenderDeviceId, SenderUserId = m.SenderUserId, Nonce = m.Nonce,
                Ciphertext = m.Ciphertext, Signature = m.Signature, LeaseToken = m.LeaseToken, Type = m.Type,
                CreatedAt = m.CreatedAt,
                IsHidden = hidden, ModeratedByUserId = moderatorUserId, ModerationReason = hidden ? reason : null
            };
            return Task.FromResult(true);
        }
    }

    public Task<long> GetMaxSeqAsync(Guid conversationId)
    {
        lock (_lock)
        {
            var max = _messages.Where(m => m.ConversationId == conversationId)
                .Select(m => (long?)m.Seq)
                .Max() ?? 0L;
            return Task.FromResult(max);
        }
    }

    public Task<int> CountByEpochBelowAsync(Guid conversationId, int epochThreshold)
    {
        lock (_lock)
        {
            var count = _messages.Count(m => m.ConversationId == conversationId && m.EpochId < epochThreshold);
            return Task.FromResult(count);
        }
    }
}
