namespace ECHAT.Models.Dtos;

public class AuditEntry
{
    /// <summary>Id riga audit (auto-increment lato DB). 0 in scrittura, popolato solo in lettura.</summary>
    public long Id { get; init; }
    public Guid ConversationId { get; init; }
    public Guid? UserId { get; init; }
    public string Action { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string? Details { get; init; }
}

/// <summary>Filtri opzionali per la lettura admin dell'audit log.</summary>
public record AuditQueryFilter(
    Guid? ConversationId = null,
    Guid? UserId = null,
    string? Action = null,
    DateTime? Since = null,
    int Limit = 200);
