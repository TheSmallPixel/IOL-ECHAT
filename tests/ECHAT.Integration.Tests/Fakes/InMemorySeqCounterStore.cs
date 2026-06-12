using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Integration.Tests.Fakes;

public class InMemorySeqCounterStore : ISeqCounterStore
{
    private readonly Dictionary<Guid, SeqCounter> _counters = new();
    private readonly object _lock = new();

    public Task<SeqCounter> GetAsync(Guid conversationId)
    {
        lock (_lock)
        {
            if (_counters.TryGetValue(conversationId, out var counter))
                return Task.FromResult(counter);

            return Task.FromResult(new SeqCounter
            {
                ConversationId = conversationId,
                NextSeq = 1,
                AnchorSeq = 0,
                AnchorEnvelopeHash = Array.Empty<byte>()
            });
        }
    }

    public Task UpdateAnchorAsync(Guid conversationId, long anchorSeq, byte[] anchorEnvelopeHash)
    {
        lock (_lock)
        {
            var current = _counters.TryGetValue(conversationId, out var existing)
                ? existing
                : new SeqCounter { ConversationId = conversationId };

            if (anchorSeq <= current.AnchorSeq) return Task.CompletedTask;

            _counters[conversationId] = new SeqCounter
            {
                ConversationId = conversationId,
                NextSeq = Math.Max(current.NextSeq, anchorSeq + 1),
                AnchorSeq = anchorSeq,
                AnchorEnvelopeHash = anchorEnvelopeHash
            };
            return Task.CompletedTask;
        }
    }

    public Task<SeqRangeReservation> ReserveRangeAsync(Guid conversationId, int count, long maxMessageSeq)
    {
        lock (_lock)
        {
            var current = _counters.TryGetValue(conversationId, out var existing)
                ? existing
                : new SeqCounter
                {
                    ConversationId = conversationId,
                    NextSeq = 1,
                    AnchorSeq = 0,
                    AnchorEnvelopeHash = Array.Empty<byte>()
                };

            var minStart = current.AnchorSeq + 1;
            if (current.AnchorSeq == 0)
                minStart = Math.Max(minStart, maxMessageSeq + 1);
            var startSeq = Math.Max(current.NextSeq, minStart);
            var endSeq = startSeq + count - 1;

            _counters[conversationId] = new SeqCounter
            {
                ConversationId = conversationId,
                NextSeq = endSeq + 1,
                AnchorSeq = current.AnchorSeq,
                AnchorEnvelopeHash = current.AnchorEnvelopeHash
            };

            return Task.FromResult(new SeqRangeReservation
            {
                StartSeq = startSeq,
                EndSeq = endSeq,
                AnchorSeq = current.AnchorSeq,
                AnchorEnvelopeHash = current.AnchorEnvelopeHash
            });
        }
    }
}
