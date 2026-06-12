using ECHAT.Models.Domain;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly EchatDbContext _db;

    public MessageRepository(EchatDbContext db)
    {
        _db = db;
    }

    public async Task AppendAsync(MessageEnvelope envelope)
    {
        _db.Messages.Add(new MessageEntity
        {
            ConversationId = envelope.ConversationId,
            MessageId = envelope.MessageId,
            Seq = envelope.Seq,
            EpochId = envelope.EpochId,
            SenderDeviceId = envelope.SenderDeviceId,
            SenderUserId = envelope.SenderUserId,
            Nonce = envelope.Nonce,
            Ciphertext = envelope.Ciphertext,
            Signature = envelope.Signature,
            LeaseToken = envelope.LeaseToken,
            Type = envelope.Type,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task<List<MessageEnvelope>> QueryAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        // Semantica della paginazione a cursore:
        //   afterSeq=N            -> prossimi `limit` messaggi con seq > N (ascendente), per il live tail.
        //   beforeSeq=N           -> ultimi `limit` messaggi con seq < N (restituiti ascendenti), scroll infinito.
        //   nessun filtro         -> ultimi `limit` messaggi (restituiti ascendenti), apertura iniziale.
        //
        // Per prendere gli ultimi N ordiniamo discendente e poi invertiamo, così il client vede
        // sempre una lista per seq crescente a cui può fare append.
        var baseQuery = _db.Messages.Where(m => m.ConversationId == conversationId);

        if (afterSeq.HasValue)
        {
            var entities = await baseQuery
                .Where(m => m.Seq > afterSeq.Value)
                .OrderBy(m => m.Seq)
                .Take(limit)
                .ToListAsync();
            return entities.Select(ToEnvelope).ToList();
        }

        // Caso default + beforeSeq: prendi gli ultimi `limit` e poi inverti in ascendente.
        var newest = beforeSeq.HasValue
            ? baseQuery.Where(m => m.Seq < beforeSeq.Value)
            : baseQuery;

        var latest = await newest
            .OrderByDescending(m => m.Seq)
            .Take(limit)
            .ToListAsync();
        latest.Reverse();
        return latest.Select(ToEnvelope).ToList();
    }

    public async Task<List<MessageEnvelope>> QueryLatestAsync(Guid conversationId, int count)
    {
        var entities = await _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Seq)
            .Take(count)
            .ToListAsync();

        return entities.Select(ToEnvelope).ToList();
    }

    public async Task<bool> ExistsAsync(Guid messageId)
    {
        return await _db.Messages.AnyAsync(m => m.MessageId == messageId);
    }

    public async Task<bool> HasMessageFromDeviceAsync(Guid conversationId, Guid senderDeviceId)
    {
        return await _db.Messages.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.SenderDeviceId == senderDeviceId);
    }

    public async Task<long> GetMaxSeqAsync(Guid conversationId)
    {
        return await _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .Select(m => (long?)m.Seq)
            .MaxAsync() ?? 0L;
    }

    public async Task<int> CountByEpochBelowAsync(Guid conversationId, int epochThreshold)
    {
        return await _db.Messages
            .CountAsync(m => m.ConversationId == conversationId && m.EpochId < epochThreshold);
    }

    public async Task<MessageEnvelope?> GetBySeqAsync(Guid conversationId, long seq)
    {
        var e = await _db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Seq == seq);
        return e is null ? null : ToEnvelope(e);
    }

    public async Task<bool> SetModerationAsync(
        Guid conversationId, long seq, bool hidden, Guid moderatorUserId, string? reason)
    {
        var entity = await _db.Messages
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Seq == seq);
        if (entity is null) return false;

        entity.IsHidden = hidden;
        entity.ModeratedAt = DateTime.UtcNow;
        entity.ModeratedByUserId = moderatorUserId;
        entity.ModerationReason = hidden ? reason : null;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task ReplaceAsync(long seq, MessageEnvelope newEnvelope)
    {
        var entity = await _db.Messages
            .FirstOrDefaultAsync(m => m.ConversationId == newEnvelope.ConversationId && m.Seq == seq);

        if (entity == null)
            throw new InvalidOperationException($"Message with seq {seq} not found");

        entity.MessageId = newEnvelope.MessageId;
        entity.EpochId = newEnvelope.EpochId;
        entity.SenderDeviceId = newEnvelope.SenderDeviceId;
        entity.SenderUserId = newEnvelope.SenderUserId;
        entity.Nonce = newEnvelope.Nonce;
        entity.Ciphertext = newEnvelope.Ciphertext;
        entity.Signature = newEnvelope.Signature;
        entity.LeaseToken = newEnvelope.LeaseToken;
        entity.Type = newEnvelope.Type;

        await _db.SaveChangesAsync();
    }

    private static MessageEnvelope ToEnvelope(MessageEntity e) => new()
    {
        ConversationId = e.ConversationId,
        MessageId = e.MessageId,
        Seq = e.Seq,
        EpochId = e.EpochId,
        SenderDeviceId = e.SenderDeviceId,
        SenderUserId = e.SenderUserId,
        Nonce = e.Nonce,
        Ciphertext = e.Ciphertext,
        Signature = e.Signature,
        LeaseToken = e.LeaseToken,
        Type = e.Type,
        CreatedAt = e.CreatedAt,
        IsHidden = e.IsHidden,
        ModeratedByUserId = e.ModeratedByUserId,
        ModerationReason = e.ModerationReason
    };
}
