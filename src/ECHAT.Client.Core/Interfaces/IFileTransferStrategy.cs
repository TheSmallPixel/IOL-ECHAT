namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Risultato del calcolo di partizionamento: quante parti e quanto grande ciascuna.
/// </summary>
public readonly record struct FilePartition(int PartCount, int PartSize);

/// <summary>
/// Logica pura del trasferimento file a chunk: matematica del partizionamento, buffering
/// (zero-copy quando possibile) e orchestrazione dell'upload a batch con concorrenza limitata.
/// L'I/O effettivo (HTTP PUT/GET, auth, URL, logging) resta nell'App e viene iniettato come delegate.
/// </summary>
public interface IFileTransferStrategy
{
    /// <summary>Numero di parti necessarie per <paramref name="dataLength"/> byte (0 -> 0 parti).</summary>
    FilePartition CalculatePartition(int dataLength);

    /// <summary>
    /// Estrae i byte da uno stream evitando la copia quando lo stream è già un
    /// <see cref="MemoryStream"/> con buffer esposto allineato (offset 0, length = count).
    /// </summary>
    Task<byte[]> BufferStreamAsync(Stream stream, CancellationToken ct);

    /// <summary>
    /// Carica <paramref name="partCount"/> parti in batch di al massimo MaxConcurrent in volo.
    /// Per ogni parte invoca <paramref name="uploadPartAsync"/>(partNo, offset, len). Dopo ogni
    /// batch completo notifica il numero cumulativo di parti completate via <paramref name="onProgress"/>.
    /// </summary>
    Task OrchestrateBatchedUploadAsync(
        byte[] data,
        FilePartition partition,
        Func<int, int, int, Task> uploadPartAsync,
        Action<int> onProgress,
        CancellationToken ct);
}
