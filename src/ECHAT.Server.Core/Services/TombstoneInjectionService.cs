using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <inheritdoc cref="ITombstoneInjectionService"/>
public class TombstoneInjectionService : ITombstoneInjectionService
{
    private readonly IMessageRepository _messages;
    private readonly ISeqCounterStore _counter;

    public TombstoneInjectionService(IMessageRepository messages, ISeqCounterStore counter)
    {
        _messages = messages;
        _counter = counter;
    }

    public async Task<TombstoneInjectionResult> InjectTombstonesAsync(
        Guid conversationId,
        Guid senderUserId,
        List<TombstoneSpec> specs)
    {
        if (specs is null || specs.Count == 0)
            return TombstoneInjectionResult.Failed("No tombstones provided.");

        var current = await _counter.GetAsync(conversationId);
        var ordered = specs.OrderBy(t => t.Seq).ToList();

        if (ordered[0].Seq <= current.AnchorSeq)
            return TombstoneInjectionResult.Failed(
                $"Tombstone seq {ordered[0].Seq} <= current anchor {current.AnchorSeq}.");

        long lastSeq = current.AnchorSeq;
        byte[] lastHash = current.AnchorEnvelopeHash;

        foreach (var t in ordered)
        {
            var envelope = new MessageEnvelope
            {
                ConversationId = conversationId,
                MessageId = t.MessageId == Guid.Empty ? Guid.NewGuid() : t.MessageId,
                Seq = t.Seq,
                EpochId = t.EpochId,
                SenderDeviceId = senderUserId,
                Nonce = t.Nonce ?? Array.Empty<byte>(),
                Ciphertext = t.Ciphertext ?? Array.Empty<byte>(),
                Signature = t.Signature ?? Array.Empty<byte>(),
                LeaseToken = string.Empty,
                Type = MessageType.GapTombstone
            };

            await _messages.AppendAsync(envelope);
            lastHash = EnvelopeHasher.Compute(envelope);
            lastSeq = envelope.Seq;
        }

        await _counter.UpdateAnchorAsync(conversationId, lastSeq, lastHash);

        return new TombstoneInjectionResult
        {
            Count = ordered.Count,
            FromSeq = ordered[0].Seq,
            ToSeq = ordered[^1].Seq,
            AnchorSeq = lastSeq,
            LastMessageId = ordered[^1].MessageId,
            LastEpochId = ordered[^1].EpochId
        };
    }
}
