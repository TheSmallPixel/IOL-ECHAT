namespace ECHAT.Server.App.Data.Entities;

public class KeyEnvelopeEntity
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public int EpochId { get; set; }
    public Guid DeviceId { get; set; }
    public byte[] WrappedCek { get; set; } = Array.Empty<byte>();
    /// <summary>0x00 = legacy, 0x01 = RSA-OAEP-2048 v1 (vedi redesign E2EE).</summary>
    public byte KeyWrapVersion { get; set; }
}
