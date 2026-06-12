using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class UserStore : IUserStore
{
    private readonly EchatDbContext _db;
    private readonly UserUpsertService _upsert;

    public UserStore(EchatDbContext db, UserUpsertService upsert)
    {
        _db = db;
        _upsert = upsert;
    }

    public async Task<UserRecord?> FindByIdAsync(Guid userId)
    {
        var entity = await _db.Users.FindAsync(userId);
        return entity is null ? null : Map(entity);
    }

    public async Task<UserRecord?> FindByGoogleSubAsync(string googleSubjectId)
    {
        var entity = await _db.Users.FirstOrDefaultAsync(u => u.GoogleSubjectId == googleSubjectId);
        return entity is null ? null : Map(entity);
    }

    public Task<bool> ExistsAsync(Guid userId)
        => _db.Users.AnyAsync(u => u.Id == userId);

    public async Task<bool> IsPlatformAdminAsync(Guid userId)
    {
        var role = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.PlatformRole)
            .FirstOrDefaultAsync();
        return role == "PlatformAdmin";
    }

    public async Task<UserRecord> UpsertGoogleUserAsync(GoogleUserUpsert upsert)
    {
        var entity = await _db.Users.FirstOrDefaultAsync(u => u.GoogleSubjectId == upsert.GoogleSubjectId);

        var existing = entity is null
            ? null
            : new ExistingUserSnapshot
            {
                DisplayName = entity.DisplayName,
                PictureUrl = entity.PictureUrl,
                CreatedAt = entity.CreatedAt
            };

        var merged = _upsert.BuildOrUpdate(upsert, existing, DateTime.UtcNow);

        if (entity is null)
        {
            entity = new UserEntity
            {
                Id = Guid.NewGuid(),
                GoogleSubjectId = upsert.GoogleSubjectId,
                Email = merged.Email,
                DisplayName = merged.DisplayName,
                PictureUrl = merged.PictureUrl,
                CreatedAt = merged.CreatedAt,
                LastLoginAt = merged.LastLoginAt
            };
            _db.Users.Add(entity);
        }
        else
        {
            entity.Email = merged.Email;
            entity.DisplayName = merged.DisplayName;
            entity.PictureUrl = merged.PictureUrl;
            entity.LastLoginAt = merged.LastLoginAt;
        }
        await _db.SaveChangesAsync();
        return Map(entity);
    }

    public async Task<PlatformStats> GetStatsAsync()
    {
        var totalUsers = await _db.Users.CountAsync(u => u.IsActive);
        var totalConversations = await _db.Conversations.CountAsync();
        var totalMessages = await _db.Messages.CountAsync();
        return new PlatformStats(totalUsers, totalConversations, totalMessages);
    }

    public async Task<List<UserWithActivity>> ListUsersWithActivityAsync()
    {
        // OrderByDescending PRIMA della Select: il provider MySQL non riesce a tradurre l'ORDER BY
        // applicato a un proiettato (UserWithActivity) che contiene scalari da sub-query
        // (_db.Members.Count + _db.Messages.Count) più il cast implicito DateTime -> DateTime?.
        // Ordinare sulla colonna grezza dell'entity produce un ORDER BY translabile in SQL puro
        // e la Select scorrendo conserva l'ordine. InMemory (usato nei test) perdonava il pattern
        // originale perché valuta client-side; MySQL Pomelo no.
        return await _db.Users
            .OrderByDescending(u => u.LastLoginAt)
            .Select(u => new UserWithActivity(
                u.Id, u.Email, u.DisplayName, u.PictureUrl,
                u.PlatformRole, u.IsActive, u.CreatedAt, u.LastLoginAt,
                _db.Members.Count(m => m.UserId == u.Id && m.RemovedAt == null),
                // Per-utente: contiamo per SenderUserId (l'identità reale), NON per SenderDeviceId
                // (id del device fisico, che non coincide mai con lo UserId  conteggio sempre 0).
                _db.Messages.Count(m => m.SenderUserId == u.Id)))
            .ToListAsync();
    }

    public async Task<UserDetail?> GetUserDetailAsync(Guid userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return null;

        var conversations = await _db.Members
            .Where(m => m.UserId == userId && m.RemovedAt == null)
            .Join(_db.Conversations, m => m.ConversationId, c => c.Id,
                (m, c) => new UserConversationSummary(c.Id, c.Name ?? string.Empty, c.CreatedAt, m.Role))
            .ToListAsync();

        var messageCount = await _db.Messages.CountAsync(m => m.SenderUserId == userId);

        return new UserDetail(
            user.Id, user.Email, user.DisplayName, user.PictureUrl,
            user.PlatformRole, user.IsActive, user.CreatedAt, user.LastLoginAt,
            messageCount, conversations);
    }

    public async Task<bool> SetPlatformRoleAsync(Guid userId, string role)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return false;
        user.PlatformRole = role;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetActiveAsync(Guid userId, bool active)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return false;
        user.IsActive = active;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<UserSearchCandidate>> SearchUsersAsync(Guid currentUserId, string query)
    {
        return await _db.Users
            .Where(u => u.IsActive && u.Id != currentUserId &&
                        (u.Email.Contains(query) || u.DisplayName.Contains(query)))
            .Select(u => new UserSearchCandidate(u.Id, u.Email, u.DisplayName, u.PictureUrl, u.IsActive))
            .Take(20)
            .ToListAsync();
    }

    private static UserRecord Map(UserEntity entity) => new()
    {
        Id = entity.Id,
        GoogleSubjectId = entity.GoogleSubjectId,
        Email = entity.Email,
        DisplayName = entity.DisplayName,
        PictureUrl = entity.PictureUrl,
        PlatformRole = entity.PlatformRole,
        IsActive = entity.IsActive,
        CreatedAt = entity.CreatedAt,
        LastLoginAt = entity.LastLoginAt
    };
}
