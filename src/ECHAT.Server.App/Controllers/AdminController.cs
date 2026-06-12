using ECHAT.Models.Dtos;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

// Catena di autorizzazione:
//   [Authorize]           -> senza JWT valido: 401
//   [RequirePlatformAdmin] -> utente autenticato non-admin: 403 (delega a IPolicyEnforcer,
//                             Core/testabile) PRIMA di entrare nell'action.
//   PlatformAdmin          -> 200
// Il gating vive interamente nel filtro: PlatformStatisticsService non riverifica più il ruolo.
[Authorize]
[RequirePlatformAdmin]
[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly PlatformStatisticsService _stats;

    public AdminController(PlatformStatisticsService stats)
    {
        _stats = stats;
    }

    /// <summary>Statistiche della piattaforma: utenti, conversazioni e messaggi totali.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var s = await _stats.GetStatsAsync();
        return Ok(new
        {
            TotalUsers = s.TotalUsers,
            TotalConversations = s.TotalConversations,
            TotalMessages = s.TotalMessages
        });
    }

    /// <summary>Elenco utenti con relative statistiche di attività.</summary>
    [HttpGet("users")]
    public async Task<IActionResult> ListUsers()
    {
        var users = await _stats.ListUsersAsync();
        return Ok(users);
    }

    /// <summary>Dettaglio statistiche di un singolo utente.</summary>
    [HttpGet("users/{userId}")]
    public async Task<IActionResult> GetUserDetail(Guid userId)
    {
        var detail = await _stats.GetUserDetailAsync(userId);
        return Ok(detail);
    }

    /// <summary>Aggiorna il ruolo di piattaforma dell'utente.</summary>
    [HttpPost("users/{userId}/role")]
    public async Task<IActionResult> SetUserRole(Guid userId, [FromBody] SetRoleRequest request)
    {
        await _stats.SetUserRoleAsync(userId, request.Role);
        return Ok(new { message = $"User role updated to {request.Role}." });
    }

    /// <summary>Disattiva un utente.</summary>
    [HttpPost("users/{userId}/deactivate")]
    public async Task<IActionResult> DeactivateUser(Guid userId)
    {
        await _stats.SetUserActiveAsync(userId, active: false);
        return Ok(new { message = "User deactivated." });
    }

    /// <summary>Riattiva un utente.</summary>
    [HttpPost("users/{userId}/activate")]
    public async Task<IActionResult> ActivateUser(Guid userId)
    {
        await _stats.SetUserActiveAsync(userId, active: true);
        return Ok(new { message = "User activated." });
    }

    /// <summary>
    /// Lettura paginata del log di audit. Tutti i parametri sono opzionali: senza filtri
    /// restituisce le ultime <c>limit</c> voci ordinate per timestamp desc. Limit viene
    /// clampato in [1, 500] lato repository per non DoS-are il server.
    /// </summary>
    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit(
        [FromQuery] Guid? conversationId,
        [FromQuery] Guid? userId,
        [FromQuery] string? action,
        [FromQuery] DateTime? since,
        [FromQuery] int limit = 200)
    {
        var entries = await _stats.QueryAuditAsync(
            new AuditQueryFilter(conversationId, userId, action, since, limit));
        return Ok(entries);
    }
}

public class SetRoleRequest
{
    public string Role { get; set; } = "User";
}
