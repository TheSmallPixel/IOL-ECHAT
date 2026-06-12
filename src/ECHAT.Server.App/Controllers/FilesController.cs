using System.Security.Claims;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

[Authorize]
[ApiController]
[Route("api/conversations/{conversationId}/files")]
public class FilesController : ControllerBase
{
    private readonly IBlobStorageService _blobService;
    private readonly QuotaService _quota;
    private readonly IAuditLog _audit;

    public FilesController(
        IBlobStorageService blobService,
        QuotaService quota,
        IAuditLog audit)
    {
        _blobService = blobService;
        _quota = quota;
        _audit = audit;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpPost("begin")]
    [RequireConversationPermission(Permission.Upload)]
    public async Task<IActionResult> BeginUpload(Guid conversationId)
    {
        var userId = GetUserId();
        if (!_quota.TryConsume($"upload:{userId}"))
            return StatusCode(429, new { error = "Upload rate limit exceeded." });

        var session = await _blobService.BeginUploadAsync(conversationId, userId);

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "FileUploadStarted",
            Timestamp = DateTime.UtcNow,
            Details = $"fileId={session.FileId}"
        });

        return Ok(session);
    }

    private const int MaxPartBytes = 2 * 1024 * 1024; // cap per chunk: 2 MiB

    [HttpPut("{fileId}/parts/{partNo}")]
    [RequestSizeLimit(MaxPartBytes + 1024)]
    [RequireConversationPermission(Permission.Upload)]
    public async Task<IActionResult> UploadPart(Guid conversationId, Guid fileId, int partNo, [FromQuery] string uploadToken)
    {
        var userId = GetUserId();
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        if (ms.Length > MaxPartBytes)
            return BadRequest(new { error = $"Part exceeds {MaxPartBytes} bytes." });

        try
        {
            await _blobService.StorePartAsync(conversationId, userId, fileId, uploadToken ?? string.Empty, partNo, ms.ToArray());
        }
        catch (UnauthorizedAccessException)
        {
            // S9: token/sessione non valida per questo fileId  non rivelare dettagli.
            return Forbid();
        }
        return Ok();
    }

    [HttpPost("{fileId}/finalize")]
    [RequireConversationPermission(Permission.Upload)]
    public async Task<IActionResult> Finalize(Guid conversationId, Guid fileId, [FromQuery] string uploadToken)
    {
        var userId = GetUserId();
        FileCommitResult result;
        try
        {
            result = await _blobService.FinalizeAsync(conversationId, userId, fileId, uploadToken ?? string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            // S9: solo chi ha aperto la sessione (token+conversazione+utente) può finalizzare.
            return Forbid();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "FileUploadFinalized",
            Timestamp = DateTime.UtcNow,
            Details = $"fileId={fileId};size={result.Size}"
        });

        return Ok(result);
    }

    [HttpGet("{fileId}")]
    [RequireConversationPermission(Permission.Download)]
    public async Task<IActionResult> Download(Guid conversationId, Guid fileId)
    {
        var userId = GetUserId();
        if (!_quota.TryConsume($"download:{userId}"))
            return StatusCode(429, new { error = "Download rate limit exceeded." });

        Stream stream;
        try
        {
            // S6: ReadAsync verifica che il blob appartenga a questa conversazione; in caso di
            // mismatch lancia FileNotFoundException così rispondiamo 404 (no existence oracle).
            stream = await _blobService.ReadAsync(conversationId, fileId);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = userId,
            Action = "FileDownloaded",
            Timestamp = DateTime.UtcNow,
            Details = $"fileId={fileId}"
        });

        return File(stream, "application/octet-stream");
    }
}
