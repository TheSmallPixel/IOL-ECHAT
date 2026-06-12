using ECHAT.Models.Enums;

namespace ECHAT.Models.Domain;

public class DecryptedMessage
{
    public Guid MessageId { get; init; }
    public long Seq { get; init; }
    public int EpochId { get; init; }
    public MessageType Type { get; init; }
    public MessagePayload Payload { get; init; } = null!;
    public Guid SenderDeviceId { get; init; }
    public Guid SenderUserId { get; init; }
    public bool IsVerified { get; init; }
    public bool SeqValid { get; init; }
    public bool ChainValid { get; init; }
    public bool DecryptionValid { get; init; }
    public bool MacValid { get; init; }
    public bool Invisible { get; init; }
    public DateTime CreatedAt { get; init; }

    // ---- Moderazione (server-side) ----
    public bool IsHidden { get; init; }
    public Guid? ModeratedByUserId { get; init; }
    public string? ModerationReason { get; init; }
}
