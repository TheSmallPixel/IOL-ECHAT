namespace ECHAT.Server.Core.Interfaces;

/// <summary>
/// Storage dei boundary di catena: ogni record marca un seq dopo il quale la chain è stata
/// legittimamente ricostruita (vedi <c>ChainBoundaryEntity</c>). Il client validator li usa
/// per non urlare "Chain: Break" sull'envelope di confine post-FullReencrypt.
/// </summary>
public interface IChainBoundaryStore
{
    Task AddAsync(Guid conversationId, long afterSeq, int atEpoch);

    /// <summary>Restituisce tutti i boundary per la conversazione, ordinati per AfterSeq.</summary>
    Task<List<ChainBoundaryRecord>> ListAsync(Guid conversationId);
}

public record ChainBoundaryRecord(long AfterSeq, int AtEpoch, DateTime CreatedAt);
