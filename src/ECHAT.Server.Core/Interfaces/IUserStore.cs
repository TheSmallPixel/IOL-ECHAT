namespace ECHAT.Server.Core.Interfaces;

public interface IUserStore
{
    Task<UserRecord?> FindByIdAsync(Guid userId);
    Task<UserRecord?> FindByGoogleSubAsync(string googleSubjectId);
    Task<bool> ExistsAsync(Guid userId);
    Task<bool> IsPlatformAdminAsync(Guid userId);
    Task<UserRecord> UpsertGoogleUserAsync(GoogleUserUpsert upsert);
    Task<PlatformStats> GetStatsAsync();
    Task<List<UserWithActivity>> ListUsersWithActivityAsync();
    Task<UserDetail?> GetUserDetailAsync(Guid userId);
    Task<bool> SetPlatformRoleAsync(Guid userId, string role);
    Task<bool> SetActiveAsync(Guid userId, bool active);

    /// <summary>
    /// Ritorna gli utenti candidati per una ricerca testuale (match su Email o DisplayName).
    /// Il filtro/ordinamento fine e la proiezione sono responsabilità del chiamante
    /// (<c>UserSearchService</c>); l'implementazione EF può restringere lato SQL.
    /// </summary>
    Task<List<UserSearchCandidate>> SearchUsersAsync(Guid currentUserId, string query);
}

/// <summary>Riga leggera usata dalla ricerca utenti.</summary>
public record UserSearchCandidate(
    Guid Id,
    string Email,
    string DisplayName,
    string? PictureUrl,
    bool IsActive);

public class UserRecord
{
    public Guid Id { get; init; }
    public string GoogleSubjectId { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
    public string PlatformRole { get; init; } = "User";
    public bool IsActive { get; init; } = true;
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

public class GoogleUserUpsert
{
    public string GoogleSubjectId { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
}

public record PlatformStats(int TotalUsers, int TotalConversations, int TotalMessages);

public record UserWithActivity(
    Guid Id,
    string Email,
    string DisplayName,
    string? PictureUrl,
    string PlatformRole,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    int ConversationCount,
    int MessageCount);

public record UserDetail(
    Guid Id,
    string Email,
    string DisplayName,
    string? PictureUrl,
    string PlatformRole,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    int MessageCount,
    List<UserConversationSummary> Conversations);

public record UserConversationSummary(Guid Id, string Name, DateTime CreatedAt, string Role);
