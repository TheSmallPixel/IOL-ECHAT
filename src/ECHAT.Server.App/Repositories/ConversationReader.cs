using ECHAT.Server.App.Data;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class ConversationReader : IConversationReader
{
    private readonly EchatDbContext _db;

    public ConversationReader(EchatDbContext db)
    {
        _db = db;
    }

    public async Task<int?> GetCurrentEpochAsync(Guid conversationId)
    {
        return await _db.Conversations
            .Where(c => c.Id == conversationId)
            .Select(c => (int?)c.CurrentEpochId)
            .FirstOrDefaultAsync();
    }

    public async Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId)
    {
        return await _db.Members
            .Where(m => m.ConversationId == conversationId && m.RemovedAt == null)
            .Select(m => m.UserId)
            .ToListAsync();
    }
}
