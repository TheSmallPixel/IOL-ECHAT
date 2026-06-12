using System.Net.Http.Json;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Trasferimento file via HTTP con upload a chunk. Legge lo stream in memoria e carica le parti in
/// parallelo (concorrenza limitata), così i file grandi non pagano un round-trip HTTPS per chunk.
/// La cifratura è a carico del chiamante: questa classe sposta solo byte.
/// </summary>
public class HttpFileTransferManager : IFileTransferManager
{
    private readonly HttpClient _http;
    private readonly TokenAuthStateProvider _auth;
    private readonly IFileTransferStrategy _strategy;
    private readonly ILogger<HttpFileTransferManager> _logger;

    public event Action<Guid, int>? OnProgress;

    public HttpFileTransferManager(
        HttpClient http,
        TokenAuthStateProvider auth,
        IFileTransferStrategy strategy,
        ILogger<HttpFileTransferManager> logger)
    {
        _http = http;
        _auth = auth;
        _strategy = strategy;
        _logger = logger;
    }

    private async Task EnsureAuthHeaderAsync()
    {
        var token = await _auth.GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<Guid> UploadAsync(Guid conversationId, Stream file, string fileName, string mimeType, CancellationToken ct)
    {
        await EnsureAuthHeaderAsync();

        // 1. Begin upload: ottiene fileId e uploadToken.
        var beginResp = await _http.PostAsync(
            $"api/conversations/{conversationId}/files/begin",
            content: null,
            ct);
        beginResp.EnsureSuccessStatusCode();
        var session = await beginResp.Content.ReadFromJsonAsync<FileUploadSession>(cancellationToken: ct)
            ?? throw new InvalidOperationException("BeginUpload returned no session.");
        var fileId = session.FileId;

        _logger.LogInformation(
            "File upload started: fileId={FileId} conversation={ConversationId} size={SizeBytes}",
            fileId, conversationId, file.CanSeek ? file.Length : -1);

        // 2. Buffer dei byte (già cifrati). In pratica il chiamante passa un MemoryStream,
        //    quindi è di fatto una copia a costo zero (vedi FileTransferStrategy.BufferStreamAsync).
        var data = await _strategy.BufferStreamAsync(file, ct);

        // 3. Partizionamento + upload a batch a concorrenza limitata: la matematica e
        //    l'orchestrazione vivono in Client.Core; qui passiamo solo il PUT della singola parte.
        var partition = _strategy.CalculatePartition(data.Length);
        await _strategy.OrchestrateBatchedUploadAsync(
            data,
            partition,
            (partNo, offset, len) => UploadOnePart(conversationId, fileId, session.UploadToken, partNo, data, offset, len, ct),
            completed => OnProgress?.Invoke(fileId, completed),
            ct);
        var partCount = partition.PartCount;

        // 4. Finalize.
        var finalResp = await _http.PostAsync(
            $"api/conversations/{conversationId}/files/{fileId}/finalize?uploadToken={Uri.EscapeDataString(session.UploadToken)}",
            content: null,
            ct);
        finalResp.EnsureSuccessStatusCode();

        _logger.LogInformation(
            "File upload completed: fileId={FileId} conversation={ConversationId} size={SizeBytes} parts={PartCount}",
            fileId, conversationId, data.Length, partCount);

        return fileId;
    }

    private async Task UploadOnePart(
        Guid conversationId, Guid fileId, string uploadToken, int partNo,
        byte[] data, int offset, int len, CancellationToken ct)
    {
        using var content = new ByteArrayContent(data, offset, len);
        var resp = await _http.PutAsync(
            $"api/conversations/{conversationId}/files/{fileId}/parts/{partNo}?uploadToken={Uri.EscapeDataString(uploadToken)}",
            content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<Stream> DownloadAsync(Guid conversationId, Guid fileId, CancellationToken ct)
    {
        await EnsureAuthHeaderAsync();
        _logger.LogInformation(
            "File download started: fileId={FileId} conversation={ConversationId}",
            fileId, conversationId);
        var resp = await _http.GetAsync(
            $"api/conversations/{conversationId}/files/{fileId}",
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        resp.EnsureSuccessStatusCode();
        var contentLength = resp.Content.Headers.ContentLength ?? -1;
        _logger.LogInformation(
            "File download stream ready: fileId={FileId} conversation={ConversationId} size={SizeBytes}",
            fileId, conversationId, contentLength);
        return await resp.Content.ReadAsStreamAsync(ct);
    }
}
