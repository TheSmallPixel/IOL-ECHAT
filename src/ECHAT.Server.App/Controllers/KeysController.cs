using System.Security.Claims;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

[Authorize]
[ApiController]
[Route("api/conversations/{conversationId}/keys")]
public class KeysController : ControllerBase
{
    private readonly KeyAccessService _keyAccess;
    private readonly QuotaService _quota;
    private readonly ILogger<KeysController> _logger;

    public KeysController(
        KeyAccessService keyAccess,
        QuotaService quota,
        ILogger<KeysController> logger)
    {
        _keyAccess = keyAccess;
        _quota = quota;
        _logger = logger;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpGet]
    [RequireConversationPermission(Permission.Read)]
    public async Task<IActionResult> GetKeys(
        Guid conversationId,
        [FromQuery] int? epochId,
        [FromQuery] Guid? deviceId)
    {
        var userId = GetUserId();

        // Rate limit per utente autenticato (mirror di MessagesController.Send).
        if (!_quota.TryConsume($"keys:{userId}"))
        {
            _logger.LogInformation(
                "Quota exceeded: user={UserId} action=GetKeys conversation={ConversationId}",
                userId, conversationId);
            return StatusCode(429, new { error = "Key fetch rate limit exceeded." });
        }

        // Un membro può leggere solo le proprie key.
        var requestedDevice = deviceId ?? userId;
        if (!_keyAccess.ValidateDeviceOwnership(userId, requestedDevice))
            return Forbid();

        var keys = await _keyAccess.ResolveKeysAsync(conversationId, userId, epochId, deviceId);

        return Ok(keys);
    }

    /// <summary>
    /// Deposita i wrap della CEK prodotti dal client (E2EE, S1): create/grant/rotation. Chi possiede
    /// la CEK la ri-wrappa con la chiave pubblica RSA di ogni device destinatario e posta i blob qui;
    /// il server li conserva senza mai vedere la CEK in chiaro. Ristretto agli admin (Owner/Admin via
    /// <see cref="Permission.AddMember"/>): la distribuzione delle chiavi avviene a create/add/rotate,
    /// tutte operazioni admin. Così un membro semplice non può sovrascrivere/poisonare i wrap altrui
    /// (il service valida inoltre che ogni target sia un membro attivo e che il blob sia ben formato).
    /// </summary>
    [HttpPost]
    [RequireConversationPermission(Permission.AddMember)]
    public async Task<IActionResult> PostKeys(
        Guid conversationId,
        [FromBody] List<WrappedKey> wraps)
    {
        var userId = GetUserId();

        if (!_quota.TryConsume($"keys:{userId}"))
        {
            _logger.LogInformation(
                "Quota exceeded: user={UserId} action=PostKeys conversation={ConversationId}",
                userId, conversationId);
            return StatusCode(429, new { error = "Key post rate limit exceeded." });
        }

        await _keyAccess.StoreClientWrapsAsync(conversationId, wraps);
        return NoContent();
    }
}
