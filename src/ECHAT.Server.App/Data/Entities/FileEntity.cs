namespace ECHAT.Server.App.Data.Entities;

public class FileEntity
{
    public long Id { get; set; }
    public Guid FileId { get; set; }
    public Guid ConversationId { get; set; }
    public string Status { get; set; } = "Uploading";
    public string UploadToken { get; set; } = string.Empty;
    public byte[] MetaCiphertext { get; set; } = Array.Empty<byte>();
    public string? FilePath { get; set; }
    public long Size { get; set; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; }
}
