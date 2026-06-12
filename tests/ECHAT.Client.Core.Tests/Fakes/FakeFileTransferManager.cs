using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// In-memory fake del transfer manager: registra gli upload e serve i download dal blob store.
/// </summary>
public class FakeFileTransferManager : IFileTransferManager
{
    public List<(Guid conversationId, byte[] data, string fileName, string mimeType)> Uploads { get; } = new();
    public Dictionary<Guid, byte[]> Blobs { get; } = new();
    public Guid NextFileId { get; set; } = Guid.NewGuid();

    public event Action<Guid, int>? OnProgress;

    public async Task<Guid> UploadAsync(Guid conversationId, Stream file, string fileName, string mimeType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var data = ms.ToArray();
        var fileId = NextFileId;
        Uploads.Add((conversationId, data, fileName, mimeType));
        Blobs[fileId] = data;
        OnProgress?.Invoke(fileId, 1);
        return fileId;
    }

    public Task<Stream> DownloadAsync(Guid conversationId, Guid fileId, CancellationToken ct)
    {
        if (!Blobs.TryGetValue(fileId, out var data))
            throw new InvalidOperationException($"No blob for {fileId}");
        return Task.FromResult<Stream>(new MemoryStream(data));
    }
}
