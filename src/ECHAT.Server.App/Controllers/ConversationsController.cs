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
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly ConversationOperationsService _ops;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(ConversationOperationsService ops, ILogger<ConversationsController> logger)
    {
        _ops = ops;
        _logger = logger;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>Elenca le conversazioni di cui l'utente autenticato è membro.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ConversationDto>>> List()
    {
        var conversations = await _ops.ListForUserAsync(GetUserId());
        return Ok(conversations.Select(c => new ConversationDto
        {
            Id = c.Id,
            Name = c.Name,
            CurrentEpochId = c.CurrentEpochId,
            CreatedAt = c.CreatedAt,
            MyRole = c.MyRole
        }));
    }

    /// <summary>Crea una nuova conversazione. Il creatore viene aggiunto come Owner.</summary>
    [HttpPost]
    public async Task<ActionResult<ConversationDto>> Create([FromBody] CreateConversationRequest request)
    {
        var userId = GetUserId();
        var conv = await _ops.CreateAsync(request.Name, userId);
        _logger.LogInformation(
            "Conversation created: conversationId={ConversationId} ownerId={UserId}",
            conv.Id, userId);
        return Ok(new ConversationDto
        {
            Id = conv.Id,
            Name = conv.Name,
            CurrentEpochId = conv.CurrentEpochId,
            CreatedAt = conv.CreatedAt,
            MyRole = "Owner"
        });
    }

    /// <summary>Dettagli di una conversazione.</summary>
    [HttpGet("{conversationId}")]
    [RequireConversationPermission(Permission.Read)]
    public async Task<ActionResult<ConversationDto>> Get(Guid conversationId)
    {
        var conv = await _ops.GetAsync(conversationId);
        return Ok(new ConversationDto
        {
            Id = conv.Id,
            Name = conv.Name,
            CurrentEpochId = conv.CurrentEpochId,
            CreatedAt = conv.CreatedAt
        });
    }

    /// <summary>Elenca i membri di una conversazione.</summary>
    [HttpGet("{conversationId}/members")]
    [RequireConversationPermission(Permission.Read)]
    public async Task<ActionResult<List<MemberDto>>> ListMembers(Guid conversationId)
    {
        var members = await _ops.ListMembersAsync(conversationId);
        return Ok(members.Select(m => new MemberDto
        {
            Id = m.UserId,
            Email = m.Email,
            DisplayName = m.DisplayName,
            PictureUrl = m.PictureUrl,
            Role = m.Role,
            JoinedAt = m.JoinedAt
        }));
    }

    /// <summary>Aggiunge un membro alla conversazione. Solo Owner e Admin.</summary>
    [HttpPost("{conversationId}/members")]
    [RequireConversationPermission(Permission.AddMember)]
    public async Task<IActionResult> AddMember(Guid conversationId, [FromBody] AddMemberRequest request)
    {
        var requesterId = GetUserId();
        await _ops.AddMemberAsync(conversationId, requesterId, request.UserId, request.IncludeHistory);
        _logger.LogInformation(
            "Member added: conversationId={ConversationId} targetUserId={TargetUserId} requesterId={RequesterId} includeHistory={IncludeHistory}",
            conversationId, request.UserId, requesterId, request.IncludeHistory);
        return Ok(new { message = "Member added." });
    }

    /// <summary>Rimuove un membro e ruota l'epoch (forward secrecy sui messaggi futuri).</summary>
    [HttpDelete("{conversationId}/members/{targetUserId}")]
    [RequireConversationPermission(Permission.RemoveMember)]
    public async Task<IActionResult> RemoveMember(Guid conversationId, Guid targetUserId)
    {
        var requesterId = GetUserId();
        var newEpoch = await _ops.RemoveMemberAsync(conversationId, requesterId, targetUserId);
        _logger.LogInformation(
            "Member removed: conversationId={ConversationId} targetUserId={TargetUserId} requesterId={RequesterId} newEpochId={NewEpochId}",
            conversationId, targetUserId, requesterId, newEpoch);
        return Ok(new { message = "Member removed.", newEpochId = newEpoch });
    }

    /// <summary>Trasferisce la ownership della conversazione a un altro membro.</summary>
    [HttpPost("{conversationId}/transfer-ownership")]
    [RequireConversationPermission(Permission.TransferOwnership)]
    public async Task<IActionResult> TransferOwnership(Guid conversationId, [FromBody] TransferOwnershipRequest request)
    {
        var requesterId = GetUserId();
        await _ops.TransferOwnershipAsync(conversationId, requesterId, request.TargetUserId);
        _logger.LogInformation(
            "Ownership transferred: conversationId={ConversationId} fromUserId={FromUserId} toUserId={ToUserId}",
            conversationId, requesterId, request.TargetUserId);
        return Ok(new { message = "Ownership transferred." });
    }

    /// <summary>Rinomina la conversazione (Owner/Admin).</summary>
    [HttpPut("{conversationId}")]
    [RequireConversationPermission(Permission.Admin)]
    public async Task<IActionResult> Rename(Guid conversationId, [FromBody] RenameConversationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Conversation name cannot be empty." });

        await _ops.RenameAsync(conversationId, GetUserId(), request.Name);
        return Ok(new { message = "Conversation renamed." });
    }

    /// <summary>Cambia il ruolo (Admin/Member) di un membro. Riservato all'Owner.</summary>
    [HttpPost("{conversationId}/members/{targetUserId}/role")]
    [RequireConversationPermission(Permission.ManageRoles)]
    public async Task<IActionResult> ChangeRole(Guid conversationId, Guid targetUserId, [FromBody] ChangeRoleRequest request)
    {
        await _ops.ChangeRoleAsync(conversationId, GetUserId(), targetUserId, request.Role);
        return Ok(new { message = "Role changed." });
    }

    /// <summary>Cancella definitivamente la conversazione (crypto-shred). Solo Owner.</summary>
    [HttpDelete("{conversationId}")]
    [RequireConversationPermission(Permission.DeleteConversation)]
    public async Task<IActionResult> Delete(Guid conversationId)
    {
        await _ops.DeleteAsync(conversationId, GetUserId());
        return Ok(new { message = "Conversation deleted." });
    }
}

public class CreateConversationRequest
{
    public string? Name { get; set; }
}

public class RenameConversationRequest
{
    public string Name { get; set; } = string.Empty;
}

public class ChangeRoleRequest
{
    public string Role { get; set; } = string.Empty;
}

public class AddMemberRequest
{
    public Guid UserId { get; set; }
    public bool IncludeHistory { get; set; }
}

public class TransferOwnershipRequest
{
    public Guid TargetUserId { get; set; }
}
