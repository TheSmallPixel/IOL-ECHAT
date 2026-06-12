using ECHAT.Models.Domain;

namespace ECHAT.Server.Core.Interfaces;

public interface IMessageRepository
{
    Task AppendAsync(MessageEnvelope envelope);
    Task<List<MessageEnvelope>> QueryAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit);
    Task<List<MessageEnvelope>> QueryLatestAsync(Guid conversationId, int count);
    Task<bool> ExistsAsync(Guid messageId);

    /// <summary>True se <paramref name="senderDeviceId"/> ha inviato almeno un messaggio nella
    /// conversazione. Usato per scopare il lookup della chiave pubblica di un device alla sola
    /// conversazione del chiamante (verifica di mittenti storici, anche rimossi).</summary>
    Task<bool> HasMessageFromDeviceAsync(Guid conversationId, Guid senderDeviceId);

    Task ReplaceAsync(long seq, MessageEnvelope newEnvelope);

    /// <summary>Singolo envelope per (conversation, seq), o null se assente.</summary>
    Task<MessageEnvelope?> GetBySeqAsync(Guid conversationId, long seq);

    /// <summary>Imposta lo stato di moderazione (hide reversibile) di un messaggio. Ritorna false se
    /// il messaggio non esiste. Non tocca il ciphertext: la chain resta invariata.</summary>
    Task<bool> SetModerationAsync(Guid conversationId, long seq, bool hidden, Guid moderatorUserId, string? reason);

    /// <summary>Massimo seq persistito per la conversazione, o 0 se non ci sono messaggi.</summary>
    Task<long> GetMaxSeqAsync(Guid conversationId);

    /// <summary>Numero di messaggi della conversazione il cui epoch è minore di <paramref name="epochThreshold"/>.</summary>
    Task<int> CountByEpochBelowAsync(Guid conversationId, int epochThreshold);
}
