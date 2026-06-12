using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class ChainBoundaryStore : IChainBoundaryStore
{
    private readonly EchatDbContext _db;

    public ChainBoundaryStore(EchatDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Guid conversationId, long afterSeq, int atEpoch)
    {
        _db.ChainBoundaries.Add(new ChainBoundaryEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            AfterSeq = afterSeq,
            AtEpoch = atEpoch,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task<List<ChainBoundaryRecord>> ListAsync(Guid conversationId)
    {
        return await _db.ChainBoundaries
            .Where(b => b.ConversationId == conversationId)
            .OrderBy(b => b.AfterSeq)
            .Select(b => new ChainBoundaryRecord(b.AfterSeq, b.AtEpoch, b.CreatedAt))
            .ToListAsync();
    }
}
