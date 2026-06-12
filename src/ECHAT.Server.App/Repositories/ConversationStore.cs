using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class ConversationStore : IConversationStore
{
    private readonly EchatDbContext _db;

    public ConversationStore(EchatDbContext db)
    {
        _db = db;
    }

    public async Task CreateAsync(ConversationRecord conversation)
    {
        _db.Conversations.Add(new ConversationEntity
        {
            Id = conversation.Id,
            Name = conversation.Name,
            CreatedByUserId = conversation.CreatedByUserId,
            CurrentEpochId = conversation.CurrentEpochId,
            CreatedAt = conversation.CreatedAt
        });
        await _db.SaveChangesAsync();
    }

    public async Task<ConversationRecord?> GetAsync(Guid conversationId)
    {
        var entity = await _db.Conversations.FindAsync(conversationId);
        if (entity is null) return null;
        return new ConversationRecord
        {
            Id = entity.Id,
            Name = entity.Name ?? string.Empty,
            CurrentEpochId = entity.CurrentEpochId,
            CreatedAt = entity.CreatedAt,
            CreatedByUserId = entity.CreatedByUserId
        };
    }

    public async Task<int> IncrementEpochAsync(Guid conversationId)
    {
        var entity = await _db.Conversations.FindAsync(conversationId)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found");
        entity.CurrentEpochId += 1;
        await _db.SaveChangesAsync();
        return entity.CurrentEpochId;
    }

    public async Task RenameAsync(Guid conversationId, string newName)
    {
        var entity = await _db.Conversations.FindAsync(conversationId)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found");
        entity.Name = newName;
        await _db.SaveChangesAsync();
    }

    public async Task<List<ConversationSummary>> ListForUserAsync(Guid userId)
    {
        // EF Core / Pomelo non riesce a tradurre OrderByDescending su una
        // proprietà di un record posizionale costruito nella stessa pipeline
        // (ConversationSummary). Si proietta prima a tipo anonimo per
        // permettere l'ordinamento sulla colonna, poi al record finale.
        return await _db.Members
            .Where(m => m.UserId == userId && m.RemovedAt == null)
            .Join(
                _db.Conversations,
                m => m.ConversationId,
                c => c.Id,
                (m, c) => new
                {
                    c.Id,
                    c.Name,
                    c.CurrentEpochId,
                    c.CreatedAt,
                    m.Role
                })
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ConversationSummary(
                x.Id,
                x.Name ?? string.Empty,
                x.CurrentEpochId,
                x.CreatedAt,
                x.Role))
            .ToListAsync();
    }

    public Task<int> CountAllAsync() => _db.Conversations.CountAsync();
}
