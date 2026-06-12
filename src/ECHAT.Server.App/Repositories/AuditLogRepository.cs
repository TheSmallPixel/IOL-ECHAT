using ECHAT.Models.Dtos;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class AuditLogRepository : IAuditLog
{
    private readonly EchatDbContext _db;

    public AuditLogRepository(EchatDbContext db)
    {
        _db = db;
    }

    public async Task RecordAsync(AuditEntry entry)
    {
        _db.AuditLog.Add(new AuditLogEntity
        {
            ConversationId = entry.ConversationId,
            UserId = entry.UserId,
            Action = entry.Action,
            Timestamp = entry.Timestamp,
            Details = entry.Details
        });
        await _db.SaveChangesAsync();
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff)
    {
        var old = await _db.AuditLog.Where(a => a.Timestamp < cutoff).ToListAsync();
        if (old.Count == 0) return 0;
        _db.AuditLog.RemoveRange(old);
        await _db.SaveChangesAsync();
        return old.Count;
    }

    /// <summary>
    /// Filtri applicati come Where prima di Select per restare interamente in SQL (vedi commento
    /// in UserStore.ListUsersWithActivityAsync: MySQL Pomelo non traduce OrderBy applicato su una
    /// proiezione con sub-query). Limit clampato a [1, 500] così un client che chiede limit=0
    /// o limit=10_000 non può restituire dataset vuoti per errore o DoS-are il server.
    /// </summary>
    public async Task<List<AuditEntry>> QueryAsync(AuditQueryFilter filter)
    {
        var limit = Math.Clamp(filter.Limit, 1, 500);

        var q = _db.AuditLog.AsQueryable();
        if (filter.ConversationId.HasValue) q = q.Where(a => a.ConversationId == filter.ConversationId.Value);
        if (filter.UserId.HasValue)         q = q.Where(a => a.UserId == filter.UserId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Action)) q = q.Where(a => a.Action == filter.Action);
        if (filter.Since.HasValue)          q = q.Where(a => a.Timestamp >= filter.Since.Value);

        return await q
            .OrderByDescending(a => a.Timestamp)
            .Take(limit)
            .Select(a => new AuditEntry
            {
                Id = a.Id,
                ConversationId = a.ConversationId,
                UserId = a.UserId,
                Action = a.Action,
                Timestamp = a.Timestamp,
                Details = a.Details
            })
            .ToListAsync();
    }
}
