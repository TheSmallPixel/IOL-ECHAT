namespace ECHAT.Models.Dtos;

public class SeqReservation
{
    public long StartSeq { get; init; }
    public long EndSeq { get; init; }
    public string LeaseToken { get; init; } = string.Empty;
    public long AnchorSeq { get; init; }
    public byte[] AnchorEnvelopeHash { get; init; } = Array.Empty<byte>();
}
