namespace ECHAT.Server.Core.Interfaces;

public interface IConversationReader
{
    /// <summary>Epoch corrente della conversazione, o null se la conversazione non esiste.</summary>
    Task<int?> GetCurrentEpochAsync(Guid conversationId);

    /// <summary>UserId dei membri attivi (non rimossi).</summary>
    Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId);
}
