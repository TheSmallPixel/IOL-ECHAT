using System.Security.Claims;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

[Authorize]
[ApiController]
[Route("api/conversations/{conversationId}/migration")]
[RequireConversationPermission(Permission.Admin)]
public class MigrationController : ControllerBase
{
    private readonly IMigrationOrchestrator _orchestrator;
    private readonly IAuditLog _audit;

    public MigrationController(
        IMigrationOrchestrator orchestrator,
        IAuditLog audit)
    {
        _orchestrator = orchestrator;
        _audit = audit;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpPost("start")]
    public async Task<IActionResult> Start(Guid conversationId, [FromQuery] MigrationMode mode)
    {
        var userId = GetUserId();
        var jobId = await _orchestrator.StartMigrationAsync(conversationId, mode, userId);
        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "MigrationStarted",
            Timestamp = DateTime.UtcNow,
            Details = $"jobId={jobId};mode={mode}"
        });

        return Ok(new { JobId = jobId });
    }

    [HttpPost("{jobId}/checkpoint")]
    public async Task<IActionResult> Checkpoint(
        Guid conversationId, Guid jobId, [FromQuery] int batchId, [FromQuery] int progress)
    {
        await _orchestrator.CheckpointAsync(conversationId, jobId, batchId, progress);
        return Ok();
    }

    [HttpPost("{jobId}/finalize")]
    public async Task<IActionResult> Finalize(Guid conversationId, Guid jobId)
    {
        var userId = GetUserId();
        await _orchestrator.FinalizeAsync(conversationId, jobId);
        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "MigrationCompleted",
            Timestamp = DateTime.UtcNow,
            Details = $"jobId={jobId}"
        });

        return Ok();
    }

    [HttpPost("{jobId}/force-finalize")]
    public async Task<IActionResult> ForceFinalize(Guid conversationId, Guid jobId)
    {
        var userId = GetUserId();
        await _orchestrator.ForceFinalizeAsync(conversationId, jobId);
        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "MigrationForceFinalized",
            Timestamp = DateTime.UtcNow,
            Details = $"jobId={jobId};dataLossAccepted=true"
        });

        return Ok();
    }

    [HttpPost("{jobId}/cancel")]
    public async Task<IActionResult> Cancel(Guid conversationId, Guid jobId)
    {
        var userId = GetUserId();
        await _orchestrator.CancelAsync(conversationId, jobId);
        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "MigrationCancelled",
            Timestamp = DateTime.UtcNow,
            Details = $"jobId={jobId}"
        });

        return Ok();
    }
}
