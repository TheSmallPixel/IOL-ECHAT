using System.Security.Claims;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

/// <summary>
/// Endpoint solo per admin che il custode usa per riempire i buchi nella catena seq globale
/// quando un lease scade inutilizzato. Salta la normale validazione del lease perché gli slot
/// seq sono già bruciati; chi chiama deve avere <see cref="Permission.Admin"/>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/conversations/{conversationId}/tombstones")]
public class TombstonesController : ControllerBase
{
    private readonly ITombstoneInjectionService _injection;
    private readonly IRealtimeNotifier _notifier;
    private readonly IAuditLog _audit;

    public TombstonesController(
        ITombstoneInjectionService injection,
        IRealtimeNotifier notifier,
        IAuditLog audit)
    {
        _injection = injection;
        _notifier = notifier;
        _audit = audit;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpPost]
    [RequireConversationPermission(Permission.Admin)]
    public async Task<IActionResult> Inject(Guid conversationId, [FromBody] InjectTombstoneRequest request)
    {
        var userId = GetUserId();
        var result = await _injection.InjectTombstonesAsync(conversationId, userId, request.Tombstones);
        if (!result.Succeeded)
            return BadRequest(new { error = result.Error });

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "GapTombstonesInjected",
            Timestamp = DateTime.UtcNow,
            Details = $"count={result.Count};fromSeq={result.FromSeq};toSeq={result.ToSeq}"
        });

        await _notifier.NotifyAsync(conversationId, new Models.Events.MessageAvailableEvent
        {
            ConversationId = conversationId,
            Seq = result.AnchorSeq,
            MessageId = result.LastMessageId,
            EpochId = result.LastEpochId,
            Timestamp = DateTime.UtcNow
        });

        return Ok(new { count = result.Count, anchorSeq = result.AnchorSeq });
    }
}
