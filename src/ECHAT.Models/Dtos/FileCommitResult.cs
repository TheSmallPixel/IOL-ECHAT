namespace ECHAT.Models.Dtos;

public class FileCommitResult
{
    public string FilePointer { get; init; } = string.Empty;
    public long Size { get; init; }
    public byte[] Hash { get; init; } = Array.Empty<byte>();
}
