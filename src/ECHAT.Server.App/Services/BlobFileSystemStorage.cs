using System.Collections.Concurrent;
using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;

namespace ECHAT.Server.App.Services;

public class BlobFileSystemStorage : IBlobStorageService
{
    private readonly string _basePath;
    private readonly BlobFileAssemblyService _assembly;

    /// <summary>
    /// Sessioni di upload in corso (fileId  token/conversazione/proprietario). Validano S9: solo chi
    /// ha aperto la sessione (stesso token, stessa conversazione, stesso utente) può scrivere parti e
    /// finalizzare il blob. È un singleton, quindi la mappa vive per tutto il processo.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, UploadSession> _sessions = new();

    /// <summary>
    /// Cache della conversazione proprietaria di ogni blob finalizzato (fileId  conversationId).
    /// Usata da <see cref="ReadAsync"/> per la verifica S6. Persistita su disco in un sidecar così da
    /// sopravvivere ai riavvii.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Guid> _blobOwners = new();

    private sealed record UploadSession(string UploadToken, Guid ConversationId, Guid OwnerUserId);

    public BlobFileSystemStorage(IConfiguration configuration, BlobFileAssemblyService assembly)
    {
        _basePath = configuration["BlobStorage:BasePath"] ?? Path.Combine(Path.GetTempPath(), "echat-blobs");
        _assembly = assembly;
        Directory.CreateDirectory(_basePath);
    }

    public Task<FileUploadSession> BeginUploadAsync(Guid conversationId, Guid ownerUserId)
    {
        var fileId = Guid.NewGuid();
        var uploadToken = Guid.NewGuid().ToString();

        _sessions[fileId] = new UploadSession(uploadToken, conversationId, ownerUserId);

        var session = new FileUploadSession
        {
            FileId = fileId,
            UploadToken = uploadToken
        };

        return Task.FromResult(session);
    }

    public async Task StorePartAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken, int partNo, byte[] encryptedBytes)
    {
        ValidateSession(conversationId, userId, fileId, uploadToken);
        if (partNo < 0)
            throw new ArgumentOutOfRangeException(nameof(partNo));

        var tempDir = TempDir(fileId);
        Directory.CreateDirectory(tempDir);
        var partPath = _assembly.GetPartPath(tempDir, partNo);
        await File.WriteAllBytesAsync(partPath, encryptedBytes);
    }

    public async Task<FileCommitResult> FinalizeAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken)
    {
        ValidateSession(conversationId, userId, fileId, uploadToken);

        var tempDir = TempDir(fileId);
        if (!Directory.Exists(tempDir))
            throw new FileNotFoundException($"No upload parts for: {fileId}");

        var parts = _assembly.OrderPartPaths(Directory.GetFiles(tempDir));

        var finalPath = BlobPath(fileId);
        await using (var output = File.Create(finalPath + ".tmp"))
        {
            long running = 0;
            foreach (var part in parts)
            {
                var bytes = await File.ReadAllBytesAsync(part);
                await output.WriteAsync(bytes);
                running += bytes.Length;
            }
            _ = running;
        }

        long totalSize = new FileInfo(finalPath + ".tmp").Length;
        File.Move(finalPath + ".tmp", finalPath, overwrite: true);

        // Registra la conversazione proprietaria del blob (cache + sidecar persistente) per la
        // verifica di ownership in ReadAsync (S6).
        _blobOwners[fileId] = conversationId;
        await File.WriteAllTextAsync(OwnerSidecarPath(fileId), conversationId.ToString());

        Directory.Delete(tempDir, recursive: true);
        _sessions.TryRemove(fileId, out _);

        return new FileCommitResult
        {
            FilePointer = finalPath,
            Size = totalSize,
            Hash = Array.Empty<byte>()
        };
    }

    public Task<Stream> ReadAsync(Guid conversationId, Guid fileId)
    {
        var filePath = BlobPath(fileId);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Blob not found: {fileId}");

        // S6: il path è flat ({fileId}.blob.enc) e non vincola la conversazione, quindi verifichiamo
        // esplicitamente che il blob appartenga alla conversazione richiesta. Mismatch  404 (tramite
        // FileNotFoundException) per non rivelare l'esistenza di blob di altre conversazioni.
        if (GetBlobOwner(fileId) != conversationId)
            throw new FileNotFoundException($"Blob not found: {fileId}");

        return Task.FromResult<Stream>(File.OpenRead(filePath));
    }

    public Task DeleteConversationAsync(Guid conversationId, IReadOnlyCollection<Guid> fileIds)
    {
        // Cancellazione idempotente: ignora i "file not found". Non dipende dallo stato del DB
        // (le righe sono già state rimosse dal chiamante).
        if (fileIds is not null)
        {
            foreach (var fileId in fileIds)
            {
                TryDeleteFile(BlobPath(fileId));
                TryDeleteFile(BlobPath(fileId) + ".tmp");
                TryDeleteFile(OwnerSidecarPath(fileId));
                TryDeleteDirectory(TempDir(fileId));

                _blobOwners.TryRemove(fileId, out _);
                _sessions.TryRemove(fileId, out _);
            }
        }

        // Cancella anche l'eventuale directory per-conversazione (schema legacy creato da
        // BeginUploadAsync: {basePath}/{conversationId}/...).
        TryDeleteDirectory(Path.Combine(_basePath, conversationId.ToString()));

        return Task.CompletedTask;
    }

    private void ValidateSession(Guid conversationId, Guid userId, Guid fileId, string uploadToken)
    {
        if (!_sessions.TryGetValue(fileId, out var session))
            throw new UnauthorizedAccessException($"No active upload session for: {fileId}");

        if (!FixedTimeEquals(session.UploadToken, uploadToken)
            || session.ConversationId != conversationId
            || session.OwnerUserId != userId)
        {
            throw new UnauthorizedAccessException($"Upload session mismatch for: {fileId}");
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a ?? string.Empty);
        var bb = System.Text.Encoding.UTF8.GetBytes(b ?? string.Empty);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private Guid GetBlobOwner(Guid fileId)
    {
        if (_blobOwners.TryGetValue(fileId, out var cached))
            return cached;

        var sidecar = OwnerSidecarPath(fileId);
        if (File.Exists(sidecar) && Guid.TryParse(File.ReadAllText(sidecar), out var owner))
        {
            _blobOwners[fileId] = owner;
            return owner;
        }

        return Guid.Empty;
    }

    // fileId è un Guid (validato dal binding del controller), quindi ToString() è sempre sicuro da
    // interpolare nel path: nessun input non fidato finisce nei path.
    private string BlobPath(Guid fileId) => Path.Combine(_basePath, $"{fileId}.blob.enc");
    private string OwnerSidecarPath(Guid fileId) => Path.Combine(_basePath, $"{fileId}.owner");
    private string TempDir(Guid fileId) => Path.Combine(_basePath, "_temp", fileId.ToString());

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
