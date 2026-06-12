using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Statistiche piattaforma e gestione utenti. L'autorizzazione PlatformAdmin è applicata a monte
/// dal filtro [RequirePlatformAdmin] sul controller (delegato a
/// <see cref="IPolicyEnforcer.AuthorizePlatformAsync"/>); qui restano solo la logica funzionale e
/// le regole di validazione/business (ruoli validi, utente inesistente, clamp dei limiti).
/// </summary>
public class PlatformStatisticsService
{
    private readonly IUserStore _users;
    private readonly IAuditLog _audit;

    public PlatformStatisticsService(IUserStore users, IAuditLog audit)
    {
        _users = users;
        _audit = audit;
    }

    public Task<PlatformStats> GetStatsAsync()
        => _users.GetStatsAsync();

    public Task<List<UserWithActivity>> ListUsersAsync()
        => _users.ListUsersWithActivityAsync();

    public async Task<UserDetail> GetUserDetailAsync(Guid userId)
    {
        return await _users.GetUserDetailAsync(userId)
            ?? throw new NotFoundException("User not found.");
    }

    public async Task SetUserRoleAsync(Guid targetUserId, string role)
    {
        if (role != "User" && role != "PlatformAdmin")
            throw new ValidationException("Invalid role. Must be 'User' or 'PlatformAdmin'.");
        var ok = await _users.SetPlatformRoleAsync(targetUserId, role);
        if (!ok) throw new NotFoundException("User not found.");
    }

    public async Task SetUserActiveAsync(Guid targetUserId, bool active)
    {
        var ok = await _users.SetActiveAsync(targetUserId, active);
        if (!ok) throw new NotFoundException("User not found.");
    }

    public Task<List<AuditEntry>> QueryAuditAsync(AuditQueryFilter filter)
        => _audit.QueryAsync(filter);
}
