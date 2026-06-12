using ECHAT.Models.Enums;

namespace ECHAT.Models.Domain;

public class MessagePayload
{
    public long Seq { get; init; }
    public byte[] PrevEnvelopeHash { get; init; } = Array.Empty<byte>();
    public string? Text { get; init; }
    /// <summary>
    /// Formato di rendering per <see cref="Text"/>. Default <see cref="MessageFormat.Plain"/>.
    /// Sta dentro al payload cifrato: il server non lo vede. I messaggi vecchi privi di questo
    /// campo vengono deserializzati come <see cref="MessageFormat.Plain"/> e mantengono l'aspetto originale.
    /// </summary>
    public MessageFormat Format { get; init; } = MessageFormat.Plain;
    public List<AttachmentRef>? Attachments { get; init; }
    public List<Guid>? Mentions { get; init; }
    public Guid? ReplyTo { get; init; }
    public bool Invisible { get; init; }
}
