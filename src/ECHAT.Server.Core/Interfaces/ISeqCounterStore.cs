namespace ECHAT.Server.Core.Interfaces;

public interface ISeqCounterStore
{
    Task<SeqCounter> GetAsync(Guid conversationId);
    Task UpdateAnchorAsync(Guid conversationId, long anchorSeq, byte[] anchorEnvelopeHash);

    /// <summary>
    /// Riserva un range contiguo di seq partendo dal massimo tra <c>NextSeq</c>, <c>AnchorSeq+1</c>
    /// e (se l'anchor non è mai stato settato) <c>maxMessageSeq+1</c>. Avanza <c>NextSeq</c> a
    /// <c>endSeq + 1</c> e ritorna il blocco riservato + l'anchor corrente.
    /// </summary>
    Task<SeqRangeReservation> ReserveRangeAsync(Guid conversationId, int count, long maxMessageSeq);
}

public class SeqRangeReservation
{
    public long StartSeq { get; init; }
    public long EndSeq { get; init; }
    public long AnchorSeq { get; init; }
    public byte[] AnchorEnvelopeHash { get; init; } = Array.Empty<byte>();
}

public class SeqCounter
{
    public Guid ConversationId { get; init; }
    public long NextSeq { get; init; }
    public long AnchorSeq { get; init; }
    public byte[] AnchorEnvelopeHash { get; init; } = Array.Empty<byte>();
}
