using ECHAT.Server.App.Data;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class MembershipReader : IMembershipReader
{
    private readonly EchatDbContext _db;

    public MembershipReader(EchatDbContext db)
    {
        _db = db;
    }

    public async Task<string?> GetRoleAsync(Guid userId, Guid conversationId)
    {
        return await _db.Members
            .Where(m => m.ConversationId == conversationId
                        && m.UserId == userId
                        && m.RemovedAt == null)
            .Select(m => m.Role)
            .FirstOrDefaultAsync();
    }
}
