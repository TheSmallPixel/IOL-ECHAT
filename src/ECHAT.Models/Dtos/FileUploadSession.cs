namespace ECHAT.Models.Dtos;

public class FileUploadSession
{
    public Guid FileId { get; init; }
    public string UploadToken { get; init; } = string.Empty;
}
