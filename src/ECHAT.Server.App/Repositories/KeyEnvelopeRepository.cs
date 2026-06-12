using ECHAT.Models.Dtos;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class KeyEnvelopeRepository : IKeyEnvelopeStore
{
    private readonly EchatDbContext _db;

    public KeyEnvelopeRepository(EchatDbContext db)
    {
        _db = db;
    }

    public async Task<List<WrappedKey>> GetKeysAsync(Guid conversationId, int? epochId, Guid? deviceId)
    {
        var query = _db.KeyEnvelopes
            .Where(k => k.ConversationId == conversationId);

        if (epochId.HasValue)
            query = query.Where(k => k.EpochId == epochId.Value);

        if (deviceId.HasValue)
            query = query.Where(k => k.DeviceId == deviceId.Value);

        var entities = await query.ToListAsync();

        return entities.Select(e => new WrappedKey
        {
            ConversationId = e.ConversationId,
            EpochId = e.EpochId,
            DeviceId = e.DeviceId,
            WrappedCek = e.WrappedCek,
            KeyWrapVersion = e.KeyWrapVersion
        }).ToList();
    }

    /// <summary>
    /// Upsert per (conversation, epoch, device): un nuovo wrap per la stessa terna SOSTITUISCE il
    /// precedente. Necessario perché in E2EE i grant ripetuti (es. re-wrap verso tutti i device a un
    /// add-member) non devono accumulare righe duplicate per lo stesso destinatario.
    /// </summary>
    public async Task StoreWrapsAsync(Guid conversationId, List<WrappedKey> wraps)
    {
        foreach (var wrap in wraps)
        {
            var existing = await _db.KeyEnvelopes
                .Where(k => k.ConversationId == conversationId
                            && k.EpochId == wrap.EpochId
                            && k.DeviceId == wrap.DeviceId)
                .ToListAsync();
            if (existing.Count > 0)
                _db.KeyEnvelopes.RemoveRange(existing);

            _db.KeyEnvelopes.Add(new KeyEnvelopeEntity
            {
                ConversationId = conversationId,
                EpochId = wrap.EpochId,
                DeviceId = wrap.DeviceId,
                WrappedCek = wrap.WrappedCek,
                KeyWrapVersion = wrap.KeyWrapVersion
            });
        }
        await _db.SaveChangesAsync();
    }

    public async Task DeleteWrapsAsync(Guid conversationId, int epochId, Guid? deviceId)
    {
        var query = _db.KeyEnvelopes
            .Where(k => k.ConversationId == conversationId && k.EpochId == epochId);

        if (deviceId.HasValue)
            query = query.Where(k => k.DeviceId == deviceId.Value);

        var entities = await query.ToListAsync();
        _db.KeyEnvelopes.RemoveRange(entities);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteWrapsForDevicesAsync(Guid conversationId, IReadOnlyCollection<Guid> deviceIds)
    {
        if (deviceIds.Count == 0) return;

        var entities = await _db.KeyEnvelopes
            .Where(k => k.ConversationId == conversationId && deviceIds.Contains(k.DeviceId))
            .ToListAsync();
        _db.KeyEnvelopes.RemoveRange(entities);
        await _db.SaveChangesAsync();
    }
}
