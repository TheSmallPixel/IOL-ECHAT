namespace ECHAT.Models.Domain;

public class AttachmentRef
{
    public Guid FileId { get; init; }
    public byte[] WrappedFileDek { get; init; } = Array.Empty<byte>();
    public string FileName { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public long Size { get; init; }
}
