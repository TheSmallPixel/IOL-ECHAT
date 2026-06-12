using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Implementazione pura di <see cref="IFileTransferStrategy"/>. Nessuna dipendenza da HttpClient /
/// JSInterop: solo matematica del partizionamento, buffering e batching dell'upload.
/// </summary>
public class FileTransferStrategy : IFileTransferStrategy
{
    /// <summary>Parti da 2 MiB: corrisponde al RequestSizeLimit del server su /parts/{n}.</summary>
    public const int PartSize = 2 * 1024 * 1024;

    /// <summary>Massimo 4 PUT di parti in volo contemporaneamente.</summary>
    public const int MaxConcurrentParts = 4;

    public FilePartition CalculatePartition(int dataLength)
    {
        var partCount = dataLength == 0 ? 0 : (dataLength + PartSize - 1) / PartSize;
        return new FilePartition(partCount, PartSize);
    }

    public async Task<byte[]> BufferStreamAsync(Stream stream, CancellationToken ct)
    {
        // Caso comune: il chiamante passa un MemoryStream già pronto -> copia a costo zero.
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == ms.Length)
        {
            return seg.Array!.Length == seg.Count ? seg.Array : seg.AsSpan().ToArray();
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public async Task OrchestrateBatchedUploadAsync(
        byte[] data,
        FilePartition partition,
        Func<int, int, int, Task> uploadPartAsync,
        Action<int> onProgress,
        CancellationToken ct)
    {
        // Carica fino a MaxConcurrentParts alla volta. Evitiamo SemaphoreSlim perché il suo lock
        // interno va in conflitto con TimerQueue sotto WasmEnableThreads. Invece dividiamo le parti
        // in batch e usiamo Task.WhenAll per ciascun batch: stessa parallelizzazione effettiva,
        // senza lock condiviso né ContinueWith.
        var completed = 0;

        for (var batchStart = 0; batchStart < partition.PartCount; batchStart += MaxConcurrentParts)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + MaxConcurrentParts, partition.PartCount);
            var batch = new Task[batchEnd - batchStart];

            for (var i = 0; i < batch.Length; i++)
            {
                var partNo = batchStart + i;
                var offset = partNo * partition.PartSize;
                var len = Math.Min(partition.PartSize, data.Length - offset);

                batch[i] = uploadPartAsync(partNo, offset, len);
            }

            await Task.WhenAll(batch);

            completed += batch.Length;
            onProgress(completed);
        }
    }
}
