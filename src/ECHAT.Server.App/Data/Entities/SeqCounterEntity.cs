namespace ECHAT.Server.App.Data.Entities;

public class SeqCounterEntity
{
    public Guid ConversationId { get; set; }
    public long NextSeq { get; set; } = 1;
    public long AnchorSeq { get; set; }
    public byte[] AnchorEnvelopeHash { get; set; } = Array.Empty<byte>();
}
