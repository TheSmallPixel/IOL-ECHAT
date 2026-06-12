namespace ECHAT.Server.Core.Interfaces;

public interface IMemberStore
{
    Task<MembershipRecord?> GetActiveAsync(Guid conversationId, Guid userId);
    Task AddAsync(Guid conversationId, Guid userId, string role);
    Task<bool> SoftRemoveAsync(Guid conversationId, Guid userId);
    Task SetRoleAsync(Guid conversationId, Guid userId, string role);
    Task<List<MemberWithUser>> ListActiveWithUserAsync(Guid conversationId);
}

public record MembershipRecord(Guid ConversationId, Guid UserId, string Role, DateTime JoinedAt);

public record MemberWithUser(
    Guid UserId,
    string Email,
    string DisplayName,
    string? PictureUrl,
    string Role,
    DateTime JoinedAt);
