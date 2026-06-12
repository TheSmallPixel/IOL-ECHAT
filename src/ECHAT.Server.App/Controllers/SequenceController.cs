using System.Security.Claims;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

[Authorize]
[ApiController]
[Route("api/conversations/{conversationId}/seq")]
public class SequenceController : ControllerBase
{
    private const int MaxLeaseSize = 256;

    private readonly ISequenceService _sequenceService;
    private readonly QuotaService _quota;
    private readonly ILogger<SequenceController> _logger;

    public SequenceController(
        ISequenceService sequenceService,
        QuotaService quota,
        ILogger<SequenceController> logger)
    {
        _sequenceService = sequenceService;
        _quota = quota;
        _logger = logger;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpPost("reserve")]
    [RequireConversationPermission(Permission.Write)]
    public async Task<IActionResult> Reserve(
        Guid conversationId,
        [FromQuery] int count = 32)
    {
        var userId = GetUserId();
        if (count <= 0 || count > MaxLeaseSize)
            return BadRequest(new { error = $"count must be in [1,{MaxLeaseSize}]" });

        // Rate limit per utente autenticato (mirror di MessagesController.Send).
        if (!_quota.TryConsume($"seq:{userId}"))
        {
            _logger.LogInformation(
                "Quota exceeded: user={UserId} action=ReserveSeq conversation={ConversationId}",
                userId, conversationId);
            return StatusCode(429, new { error = "Sequence reservation rate limit exceeded." });
        }

        // L'identità della reservation/lease è SEMPRE l'utente autenticato: non ci fidiamo di
        // un deviceId fornito dal client (sarebbe spoofabile per riservare seq a nome altrui o
        // per bypassare il rate limit). Se in futuro serve granularità per device, il deviceId
        // va validato come fa KeysController (deviceId == userId) prima di usarlo.
        var reservation = await _sequenceService.ReserveRangeAsync(
            conversationId,
            userId,
            count);

        return Ok(reservation);
    }
}
