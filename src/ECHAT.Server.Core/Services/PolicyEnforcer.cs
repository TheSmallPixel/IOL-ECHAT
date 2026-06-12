using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ECHAT.Server.Core.Services;

public class PolicyEnforcer : IPolicyEnforcer
{
    private readonly IMembershipReader _membership;
    private readonly IUserStore _users;
    private readonly ILogger<PolicyEnforcer> _logger;

    public PolicyEnforcer(IMembershipReader membership, IUserStore users, ILogger<PolicyEnforcer>? logger = null)
    {
        _membership = membership;
        _users = users;
        _logger = logger ?? NullLogger<PolicyEnforcer>.Instance;
    }

    public async Task<bool> AuthorizeAsync(Guid userId, Guid conversationId, Permission permission)
    {
        var role = await _membership.GetRoleAsync(userId, conversationId);
        if (role is null)
        {
            _logger.LogWarning(
                "Authorization denied (no membership): userId={UserId} conversationId={ConversationId} permission={Permission}",
                userId, conversationId, permission);
            return false;
        }

        var allowed = permission switch
        {
            Permission.Read or Permission.Write or Permission.Upload or Permission.Download
                => true,
            Permission.AddMember or Permission.RemoveMember
                => role is "Owner" or "Admin",
            Permission.Admin
                => role is "Owner" or "Admin",
            // Moderazione messaggi: Owner/Admin + il nuovo ruolo Moderator (meno poteri di un Admin:
            // può solo nascondere messaggi, non gestire membri/ruoli). La regola "non puoi moderare
            // chi ha ruolo >= al tuo" è applicata nel MessageModerationService, non qui.
            Permission.ModerateMessages
                => role is "Owner" or "Admin" or "Moderator",
            Permission.TransferOwnership or Permission.ManageRoles or Permission.DeleteConversation
                => role is "Owner",
            _ => false
        };

        if (allowed)
        {
            _logger.LogDebug(
                "Authorization granted: userId={UserId} conversationId={ConversationId} permission={Permission} role={Role}",
                userId, conversationId, permission, role);
        }
        else
        {
            _logger.LogWarning(
                "Authorization denied (insufficient role): userId={UserId} conversationId={ConversationId} permission={Permission} role={Role}",
                userId, conversationId, permission, role);
        }

        return allowed;
    }

    public async Task<bool> AuthorizePlatformAsync(Guid userId)
    {
        var isAdmin = await _users.IsPlatformAdminAsync(userId);
        if (!isAdmin)
        {
            _logger.LogWarning(
                "Platform authorization denied (not a platform admin): userId={UserId}",
                userId);
        }
        return isAdmin;
    }
}
