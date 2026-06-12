using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Interfaces;

public interface IConversationStore
{
    Task CreateAsync(ConversationRecord conversation);
    Task<ConversationRecord?> GetAsync(Guid conversationId);
    Task<int> IncrementEpochAsync(Guid conversationId);
    Task RenameAsync(Guid conversationId, string newName);
    Task<List<ConversationSummary>> ListForUserAsync(Guid userId);
    Task<int> CountAllAsync();
}

/// <summary>
/// Cancellazione "crypto-shred" di una conversazione: rimuove in modo permanente tutti i dati
/// associati (messaggi, membership, key envelope, contatore di sequenza, lease, chain boundary,
/// job di migrazione, file e blob cifrati) in un'unica transazione atomica. Nella stessa
/// transazione viene scritta la riga di audit <paramref name="deletionAudit"/> (il resto del log
/// append-only NON viene toccato). I blob cifrati su storage vengono cancellati DOPO il commit.
/// Astratto in Core così che <see cref="Services.ConversationOperationsService"/> resti testabile
/// senza EF; l'implementazione concreta sta in Server.App.
/// </summary>
public interface IConversationPurger
{
    Task PurgeAsync(Guid conversationId, AuditEntry deletionAudit);
}

public class ConversationRecord
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int CurrentEpochId { get; init; }
    public DateTime CreatedAt { get; init; }
    public Guid CreatedByUserId { get; init; }
}

public record ConversationSummary(
    Guid Id,
    string Name,
    int CurrentEpochId,
    DateTime CreatedAt,
    string MyRole);
