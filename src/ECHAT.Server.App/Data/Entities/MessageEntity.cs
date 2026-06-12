using ECHAT.Models.Enums;

namespace ECHAT.Server.App.Data.Entities;

public class MessageEntity
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    public long Seq { get; set; }
    public int EpochId { get; set; }
    public Guid SenderDeviceId { get; set; }
    public Guid SenderUserId { get; set; }
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[] Signature { get; set; } = Array.Empty<byte>();
    public string LeaseToken { get; set; } = string.Empty;
    public MessageType Type { get; set; }
    public DateTime CreatedAt { get; set; }

    // ---- Moderazione (hide reversibile) ----
    // Metadati SERVER-side, NON parte dell'envelope firmato: il ciphertext resta intatto (chain
    // invariata), il client onesto mostra un placeholder al posto del contenuto quando IsHidden.
    public bool IsHidden { get; set; }
    public DateTime? ModeratedAt { get; set; }
    public Guid? ModeratedByUserId { get; set; }
    public string? ModerationReason { get; set; }
}
