using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class SeqLeaseStore : ISeqLeaseStore
{
    private readonly EchatDbContext _db;

    public SeqLeaseStore(EchatDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(SeqLeaseRecord lease)
    {
        _db.SeqLeases.Add(new SeqLeaseEntity
        {
            LeaseToken = lease.LeaseToken,
            ConversationId = lease.ConversationId,
            DeviceId = lease.DeviceId,
            StartSeq = lease.StartSeq,
            EndSeq = lease.EndSeq,
            IssuedAt = lease.IssuedAt,
            ExpiresAt = lease.ExpiresAt
        });
        await _db.SaveChangesAsync();
    }

    public async Task<SeqLeaseRecord?> FindByTokenAsync(string leaseToken)
    {
        var entity = await _db.SeqLeases
            .FirstOrDefaultAsync(l => l.LeaseToken == leaseToken);
        if (entity is null) return null;

        return new SeqLeaseRecord
        {
            LeaseToken = entity.LeaseToken,
            ConversationId = entity.ConversationId,
            DeviceId = entity.DeviceId,
            StartSeq = entity.StartSeq,
            EndSeq = entity.EndSeq,
            IssuedAt = entity.IssuedAt,
            ExpiresAt = entity.ExpiresAt
        };
    }

    public async Task<int> PurgeExpiredAsync(DateTime now)
    {
        var expired = await _db.SeqLeases.Where(l => l.ExpiresAt <= now).ToListAsync();
        if (expired.Count == 0) return 0;
        _db.SeqLeases.RemoveRange(expired);
        await _db.SaveChangesAsync();
        return expired.Count;
    }
}
