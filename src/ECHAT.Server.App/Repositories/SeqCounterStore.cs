using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;

namespace ECHAT.Server.App.Repositories;

public class SeqCounterStore : ISeqCounterStore
{
    private readonly EchatDbContext _db;
    private readonly SeqCounterDomainService _domain;

    public SeqCounterStore(EchatDbContext db, SeqCounterDomainService domain)
    {
        _db = db;
        _domain = domain;
    }

    private static SeqCounter ToDto(SeqCounterEntity entity) => new()
    {
        ConversationId = entity.ConversationId,
        NextSeq = entity.NextSeq,
        AnchorSeq = entity.AnchorSeq,
        AnchorEnvelopeHash = entity.AnchorEnvelopeHash
    };

    public async Task<SeqCounter> GetAsync(Guid conversationId)
    {
        var entity = await _db.SeqCounters.FindAsync(conversationId);
        if (entity is null)
        {
            return new SeqCounter
            {
                ConversationId = conversationId,
                NextSeq = 1,
                AnchorSeq = 0,
                AnchorEnvelopeHash = Array.Empty<byte>()
            };
        }

        return new SeqCounter
        {
            ConversationId = entity.ConversationId,
            NextSeq = entity.NextSeq,
            AnchorSeq = entity.AnchorSeq,
            AnchorEnvelopeHash = entity.AnchorEnvelopeHash
        };
    }

    public async Task UpdateAnchorAsync(Guid conversationId, long anchorSeq, byte[] anchorEnvelopeHash)
    {
        var entity = await _db.SeqCounters.FindAsync(conversationId);
        if (entity is null)
        {
            entity = new SeqCounterEntity
            {
                ConversationId = conversationId,
                NextSeq = anchorSeq + 1,
                AnchorSeq = anchorSeq,
                AnchorEnvelopeHash = anchorEnvelopeHash
            };
            _db.SeqCounters.Add(entity);
        }
        else if (_domain.CanUpdateAnchor(ToDto(entity), anchorSeq))
        {
            entity.AnchorSeq = anchorSeq;
            entity.AnchorEnvelopeHash = anchorEnvelopeHash;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<SeqRangeReservation> ReserveRangeAsync(Guid conversationId, int count, long maxMessageSeq)
    {
        var entity = await _db.SeqCounters.FindAsync(conversationId);
        if (entity is null)
        {
            entity = new SeqCounterEntity
            {
                ConversationId = conversationId,
                NextSeq = 1,
                AnchorSeq = 0,
                AnchorEnvelopeHash = Array.Empty<byte>()
            };
            _db.SeqCounters.Add(entity);
        }

        var result = _domain.ReserveRange(ToDto(entity), count, maxMessageSeq);
        entity.NextSeq = result.NewNextSeq;
        await _db.SaveChangesAsync();
        return result.Reservation;
    }
}
