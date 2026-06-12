using System.Security.Claims;
using ECHAT.Models.Domain;
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
[Route("api/conversations/{conversationId}/messages")]
public class MessagesController : ControllerBase
{
    private readonly IMessageIngestPipeline _ingestPipeline;
    private readonly IMessageRepository _messageRepository;
    private readonly QuotaService _quota;
    private readonly IMigrationOrchestrator _migrations;
    private readonly IChainBoundaryStore _chainBoundaries;
    private readonly IEnvelopeValidator _envelopeValidator;
    private readonly MessageModerationService _moderation;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        IMessageIngestPipeline ingestPipeline,
        IMessageRepository messageRepository,
        QuotaService quota,
        IMigrationOrchestrator migrations,
        IChainBoundaryStore chainBoundaries,
        IEnvelopeValidator envelopeValidator,
        MessageModerationService moderation,
        ILogger<MessagesController> logger)
    {
        _ingestPipeline = ingestPipeline;
        _messageRepository = messageRepository;
        _quota = quota;
        _migrations = migrations;
        _chainBoundaries = chainBoundaries;
        _envelopeValidator = envelopeValidator;
        _moderation = moderation;
        _logger = logger;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpPost]
    [RequestSizeLimit(2 * 1024 * 1024)] // cap richiesta: 2 MiB
    [RequireConversationPermission(Permission.Write)]
    public async Task<IActionResult> Send(Guid conversationId, [FromBody] MessageEnvelope envelope)
    {
        var userId = GetUserId();
        if (envelope.ConversationId != conversationId)
            return BadRequest(new { error = "Envelope conversationId does not match route." });

        var sizeError = _envelopeValidator.Validate(envelope);
        if (sizeError is not null)
            return BadRequest(new { error = sizeError });

        if (!_quota.TryConsume($"send:{userId}"))
        {
            _logger.LogInformation(
                "Quota exceeded: user={UserId} action=Send conversation={ConversationId}",
                userId, conversationId);
            return StatusCode(429, new { error = "Send rate limit exceeded." });
        }

        // Guardia server-side contro FullReencrypt in corso: il custode sta riscrivendo gli
        // envelope storici e accettare nuovi POST sull'epoch corrente farebbe slittare il
        // confine in modo non tracciabile. Il custode usa /messages/{seq}/replace (endpoint
        // separato, gated diversamente), quindi rifiutare TUTTI i Send qui è corretto.
        // Lato client esiste GuardAgainstActiveMigration ma è solo convenzione tra processi
        // sulla stessa sessione; questo rifiuto è la sola garanzia che vale tra device.
        var activeFullReencrypt = await _migrations.GetActiveFullReencryptJobAsync(conversationId);
        if (activeFullReencrypt is not null)
        {
            _logger.LogInformation(
                "Send rejected during FullReencrypt: user={UserId} conversation={ConversationId} jobId={JobId}",
                userId, conversationId, activeFullReencrypt.Id);
            return Conflict(new { error = "A FullReencrypt migration is in progress; retry when it completes." });
        }

        try
        {
            var ack = await _ingestPipeline.IngestAsync(envelope, userId);
            return Ok(ack);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet]
    [RequireConversationPermission(Permission.Read)]
    public async Task<IActionResult> Fetch(
        Guid conversationId,
        [FromQuery] long? afterSeq,
        [FromQuery] long? beforeSeq,
        [FromQuery] int limit = 50)
    {
        var messages = await _messageRepository.QueryAsync(conversationId, afterSeq, beforeSeq, limit);
        return Ok(messages);
    }

    /// <summary>
    /// Conta gli envelope con epoch strettamente inferiore a <paramref name="epochBelow"/>.
    /// Usato dal custode per stimare il totale prima di un FullReencrypt e mostrare progress
    /// con percentuale invece di solo contatore.
    /// </summary>
    [HttpGet("count")]
    [RequireConversationPermission(Permission.Read)]
    public async Task<IActionResult> Count(
        Guid conversationId,
        [FromQuery] int epochBelow)
    {
        var count = await _messageRepository.CountByEpochBelowAsync(conversationId, epochBelow);
        return Ok(new { count });
    }

    /// <summary>
    /// Sostituisce l'envelope a un seq noto. Endpoint usato SOLO dal custode durante FullReencrypt
    /// per riscrivere il ciphertext con il nuovo epoch. Validazioni di sicurezza in ordine:
    ///   1) Il chiamante è admin della conversazione (permesso base).
    ///   2) Esiste un job FullReencrypt InProgress per questa conversazione (gate temporale).
    ///   3) Il chiamante è il custode che ha avviato quel job (gate identità).
    ///   4) L'envelope nuovo è al target epoch del job (= epoch corrente della conversazione)
    ///       un admin non può "downgrade-attaccare" verso un epoch arbitrario.
    /// Senza queste 4 controlli, qualsiasi admin con un token JWT valido potrebbe riscrivere
    /// silenziosamente la storia (anche fuori da una migrazione legittima).
    /// </summary>
    [HttpPost("{seq:long}/replace")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [RequireConversationPermission(Permission.Admin)]
    public async Task<IActionResult> Replace(
        Guid conversationId,
        long seq,
        [FromBody] MessageEnvelope envelope)
    {
        var userId = GetUserId();
        if (envelope.ConversationId != conversationId || envelope.Seq != seq)
            return BadRequest(new { error = "Envelope (conversationId, seq) must match the route." });

        var sizeError = _envelopeValidator.Validate(envelope);
        if (sizeError is not null)
            return BadRequest(new { error = sizeError });

        // Gate temporale + identità: c'è un FullReencrypt in corso e questo utente lo guida?
        var job = await _migrations.GetActiveFullReencryptJobAsync(conversationId);
        if (job is null)
        {
            _logger.LogWarning(
                "Replace rejected: no active FullReencrypt job. user={UserId} conversation={ConversationId} seq={Seq}",
                userId, conversationId, seq);
            return Conflict(new { error = "No active FullReencrypt migration for this conversation." });
        }
        if (job.CustodianUserId != userId)
        {
            _logger.LogWarning(
                "Replace rejected: caller is not the custodian. user={UserId} custodian={CustodianUserId} jobId={JobId}",
                userId, job.CustodianUserId, job.Id);
            return Forbid();
        }

        try
        {
            await _messageRepository.ReplaceAsync(seq, envelope);
            // Aggiorna MaxReplacedSeq sul job (atomico via UPDATE conditional); FinalizeAsync lo
            // userà per scrivere il ChainBoundary giusto.
            await _migrations.RecordReplacementAsync(job.Id, seq);
            _logger.LogInformation(
                "Message replaced: conversation={ConversationId} seq={Seq} by={UserId} jobId={JobId}",
                conversationId, seq, userId, job.Id);
            return Ok();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Restituisce i boundary di catena per la conversazione: ogni boundary marca un seq dopo
    /// il quale la chain è stata legittimamente ricostruita da una saga FullReencrypt
    /// (l'envelope subito dopo punta a un hash "pre-riscrittura" e quindi appare rotto al
    /// validator). Il client carica questi boundary una volta al fetch iniziale e li aggiorna
    /// alla ricezione di JobProgressEvent(Status=Completed) via SignalR.
    /// </summary>
    [HttpGet("chain-boundaries")]
    [RequireConversationPermission(Permission.Read)]
    public async Task<IActionResult> GetChainBoundaries(Guid conversationId)
    {
        var boundaries = await _chainBoundaries.ListAsync(conversationId);
        return Ok(boundaries);
    }

    /// <summary>
    /// Modera un messaggio: lo nasconde (<c>hidden=true</c>) o lo ri-mostra. Riservato a chi ha il
    /// permesso <see cref="Permission.ModerateMessages"/> (Owner/Admin/Moderator); il service applica
    /// inoltre la regola di gerarchia (non si modera chi ha ruolo superiore al proprio). "Hide" è un
    /// flag server-side: il ciphertext non viene toccato, quindi la chain resta intatta.
    /// </summary>
    [HttpPost("{seq:long}/moderate")]
    [RequireConversationPermission(Permission.ModerateMessages)]
    public async Task<IActionResult> Moderate(
        Guid conversationId,
        long seq,
        [FromBody] ModerateMessageRequest request)
    {
        var userId = GetUserId();
        var evt = await _moderation.ModerateAsync(conversationId, userId, seq, request.Hidden, request.Reason);
        return Ok(evt);
    }

    /// <summary>
    /// Restituisce l'ultimo envelope per calcolare prevEnvelopeHash prima dell'invio.
    /// </summary>
    [HttpGet("latest")]
    [RequireConversationPermission(Permission.Read)]
    public async Task<IActionResult> GetLatest(Guid conversationId)
    {
        var messages = await _messageRepository.QueryLatestAsync(conversationId, 1);
        var latest = messages.FirstOrDefault();
        if (latest == null)
            return Ok(new { exists = false });

        return Ok(new { exists = true, envelope = latest });
    }
}
