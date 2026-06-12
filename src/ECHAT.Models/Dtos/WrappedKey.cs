namespace ECHAT.Models.Dtos;

public class WrappedKey
{
    public Guid ConversationId { get; init; }
    public int EpochId { get; init; }
    public Guid DeviceId { get; init; }
    /// <summary>
    /// CEK wrappata per il device. Dal redesign E2EE: <c>0xB2 ‖ RSA-OAEP-2048(CEK)</c> (~256 byte),
    /// mai più i 32 byte di CEK in chiaro. Il blob si auto-descrive tramite il magic byte; vedi
    /// <see cref="KeyWrapVersion"/> per il discriminante lato query.
    /// </summary>
    public byte[] WrappedCek { get; init; } = Array.Empty<byte>();
    /// <summary>0x00 = legacy/non valorizzato, 0x01 = RSA-OAEP-2048 (v1, blob con magic 0xB2).</summary>
    public byte KeyWrapVersion { get; init; }
}
