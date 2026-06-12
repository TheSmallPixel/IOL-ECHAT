namespace ECHAT.Client.Core.Interfaces;

public interface IFileTransferManager
{
    Task<Guid> UploadAsync(Guid conversationId, Stream file, string fileName, string mimeType, CancellationToken ct);
    Task<Stream> DownloadAsync(Guid conversationId, Guid fileId, CancellationToken ct);
    event Action<Guid, int>? OnProgress;
}
