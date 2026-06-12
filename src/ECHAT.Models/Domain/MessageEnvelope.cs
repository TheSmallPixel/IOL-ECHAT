using ECHAT.Models.Enums;

namespace ECHAT.Models.Domain;

public class MessageEnvelope
{
    public Guid ConversationId { get; init; }
    public Guid MessageId { get; init; }
    public long Seq { get; init; }
    public int EpochId { get; init; }
    /// <summary>Device che ha inviato il messaggio (chiave nella directory delle chiavi pubbliche).</summary>
    public Guid SenderDeviceId { get; init; }
    /// <summary>Utente proprietario del device mittente. Il server lo valida contro il JWT (S4).</summary>
    public Guid SenderUserId { get; init; }
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();
    public byte[] Signature { get; init; } = Array.Empty<byte>();
    public string LeaseToken { get; init; } = string.Empty;
    public MessageType Type { get; init; }
    public DateTime CreatedAt { get; init; }

    // ---- Moderazione (sidecar server-side; NON incluso in EnvelopeHasher né nella firma) ----
    // Popolato in lettura dal server (fetch); ignorato in ingest (un client non può auto-dichiararsi
    // moderato). Permette al client di rendere un placeholder per i messaggi nascosti.
    public bool IsHidden { get; init; }
    public Guid? ModeratedByUserId { get; init; }
    public string? ModerationReason { get; init; }
}
