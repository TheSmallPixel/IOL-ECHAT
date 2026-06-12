using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class MemberStore : IMemberStore
{
    private readonly EchatDbContext _db;

    public MemberStore(EchatDbContext db)
    {
        _db = db;
    }

    public async Task<MembershipRecord?> GetActiveAsync(Guid conversationId, Guid userId)
    {
        var entity = await _db.Members.FirstOrDefaultAsync(m =>
            m.ConversationId == conversationId && m.UserId == userId && m.RemovedAt == null);
        if (entity is null) return null;
        return new MembershipRecord(entity.ConversationId, entity.UserId, entity.Role, entity.JoinedAt);
    }

    public async Task AddAsync(Guid conversationId, Guid userId, string role)
    {
        // L'indice univoco (ConversationId, UserId) copre anche le righe soft-deleted: se l'utente
        // era stato rimosso in passato, una INSERT pulita romperebbe il vincolo. Riattiviamo la riga
        // esistente: l'auto-incremento di Id resta com'era, ma JoinedAt riparte da ora così la UI
        // mostra la nuova adesione.
        var existing = await _db.Members.FirstOrDefaultAsync(m =>
            m.ConversationId == conversationId && m.UserId == userId);
        if (existing is not null)
        {
            existing.Role = role;
            existing.JoinedAt = DateTime.UtcNow;
            existing.RemovedAt = null;
        }
        else
        {
            _db.Members.Add(new MemberEntity
            {
                ConversationId = conversationId,
                UserId = userId,
                Role = role,
                JoinedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();
    }

    public async Task<bool> SoftRemoveAsync(Guid conversationId, Guid userId)
    {
        var entity = await _db.Members.FirstOrDefaultAsync(m =>
            m.ConversationId == conversationId && m.UserId == userId && m.RemovedAt == null);
        if (entity is null) return false;
        entity.RemovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task SetRoleAsync(Guid conversationId, Guid userId, string role)
    {
        var entity = await _db.Members.FirstOrDefaultAsync(m =>
            m.ConversationId == conversationId && m.UserId == userId && m.RemovedAt == null)
            ?? throw new InvalidOperationException("Membership not found");
        entity.Role = role;
        await _db.SaveChangesAsync();
    }

    public async Task<List<MemberWithUser>> ListActiveWithUserAsync(Guid conversationId)
    {
        return await _db.Members
            .Where(m => m.ConversationId == conversationId && m.RemovedAt == null)
            .Join(_db.Users, m => m.UserId, u => u.Id,
                (m, u) => new MemberWithUser(
                    u.Id, u.Email, u.DisplayName, u.PictureUrl, m.Role, m.JoinedAt))
            .ToListAsync();
    }
}
