using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Trasporto verso il server della chat. Astratto in modo che il flusso dei messaggi (encrypt /
/// decrypt / verifica catena) viva in Client.Core ed è indipendente da HttpClient, Blazor o JWT.
/// L'implementazione concreta sta in Client.App e parla HTTP.
/// </summary>
public interface IChatServerGateway
{
    /// <summary>UserId dell'utente autenticato corrente.</summary>
    Task<Guid> GetCurrentUserIdAsync();

    /// <summary>Tutte le wrap CEK per la conversazione, opzionalmente filtrate per epoch.</summary>
    Task<List<WrappedKey>> GetKeysAsync(Guid conversationId, int? epochId = null);

    /// <summary>Ultimo envelope persistito (per estrarre il prevEnvelopeHash), o null se vuoto.</summary>
    Task<MessageEnvelope?> GetLatestEnvelopeAsync(Guid conversationId);

    /// <summary>Riserva un range di seq al server.</summary>
    Task<SeqReservation> ReserveSeqAsync(Guid conversationId, int count);

    /// <summary>Invia un envelope cifrato al server.</summary>
    Task PostMessageAsync(MessageEnvelope envelope);

    /// <summary>Nasconde/ri-mostra un messaggio (moderazione). Richiede il permesso ModerateMessages.</summary>
    Task ModerateMessageAsync(Guid conversationId, long seq, bool hidden, string? reason);

    /// <summary>Recupera envelope con paginazione a cursore.</summary>
    Task<List<MessageEnvelope>> FetchEnvelopesAsync(
        Guid conversationId, long? afterSeq, long? beforeSeq, int limit);

    /// <summary>Aggiunge un membro alla conversazione.</summary>
    Task AddMemberAsync(Guid conversationId, Guid userId, bool includeHistory);

    /// <summary>Rimuove un membro dalla conversazione.</summary>
    /// <summary>Rimuove un membro; ritorna il NUOVO epoch (autoritativo dal server) per cui il
    /// chiamante deve provisionare una CEK fresca (E2EE rotation). Evita l'assunzione oldEpoch+1 lato client.</summary>
    Task<int> RemoveMemberAsync(Guid conversationId, Guid userId);

    /// <summary>Cambia il ruolo ("Admin"/"Member") di un membro. Riservato all'Owner lato server.</summary>
    Task SetMemberRoleAsync(Guid conversationId, Guid userId, string role);

    /// <summary>Rinomina la conversazione (Owner/Admin lato server).</summary>
    Task RenameConversationAsync(Guid conversationId, string newName);

    /// <summary>Cancella definitivamente la conversazione: crypto-shred (Owner/Admin lato server).</summary>
    Task DeleteConversationAsync(Guid conversationId);

    /// <summary>Avvia una migrazione (es. strong revoke). Ritorna il JobId.</summary>
    Task<Guid> StartMigrationAsync(Guid conversationId, MigrationMode mode);

    /// <summary>Aggiorna il checkpoint di un job di migrazione.</summary>
    Task CheckpointMigrationAsync(Guid conversationId, Guid jobId, int batchId, int progressPercent);

    /// <summary>Finalizza un job di migrazione (crypto-shred + completion).</summary>
    Task FinalizeMigrationAsync(Guid conversationId, Guid jobId);

    /// <summary>
    /// Annulla un job di migrazione. Il server emette un evento Cancelled così le altre tab/admin
    /// che osservano via SignalR smettono di mostrare il banner. Nessun crypto-shred lato server.
    /// </summary>
    Task CancelMigrationAsync(Guid conversationId, Guid jobId);

    /// <summary>
    /// Force-finalize: il custode ACCETTA esplicitamente la perdita degli envelope non
    /// ri-cifrati (diventeranno permanentemente illeggibili dopo il crypto-shred). Usato
    /// quando il custode non ha le CEK per decifrare alcuni vecchi epoch.
    /// </summary>
    Task ForceFinalizeMigrationAsync(Guid conversationId, Guid jobId);

    /// <summary>Sostituisce l'envelope con un dato seq (usato dal custode in FullReencrypt).</summary>
    Task ReplaceMessageAsync(Guid conversationId, long seq, MessageEnvelope envelope);

    /// <summary>Posta i tombstone per coprire un range di seq mai usati.</summary>
    Task PostTombstonesAsync(Guid conversationId, IEnumerable<TombstoneRecord> tombstones);

    /// <summary>
    /// Conta gli envelope della conversazione con epoch strettamente inferiore a
    /// <paramref name="epochBelow"/>. Il custode lo usa prima di FullReencrypt per conoscere
    /// il totale e mostrare progress con percentuale.
    /// </summary>
    Task<int> CountEnvelopesBelowEpochAsync(Guid conversationId, int epochBelow);

    /// <summary>
    /// Restituisce i boundary di catena della conversazione. Il validator client li usa per
    /// trattare l'envelope subito dopo come "chain restart" invece di "Chain: Break", visto
    /// che il suo PrevEnvelopeHash punta a un hash pre-riscrittura.
    /// </summary>
    Task<List<ChainBoundary>> GetChainBoundariesAsync(Guid conversationId);

    /// <summary>
    /// Directory delle chiavi pubbliche dei device dei membri (E2EE). Usata in ricezione per
    /// verificare le firme (mappa SenderDeviceId  chiave pubblica ECDSA) e in invio per wrappare
    /// la CEK verso i device (chiave pubblica RSA).
    /// </summary>
    Task<List<DevicePublicKey>> GetConversationDevicesAsync(Guid conversationId);

    /// <summary>
    /// Chiave pubblica di un device che ha scritto in <paramref name="conversationId"/> (o null).
    /// Usato in ricezione per verificare la firma di mittenti non più tra i membri attivi (lookup
    /// storico lazy). Scopato alla conversazione: niente lookup globale di chiavi arbitrarie.
    /// </summary>
    Task<DevicePublicKey?> GetConversationSenderDeviceAsync(Guid conversationId, Guid deviceId);

    /// <summary>
    /// Deposita sul server i wrap della CEK prodotti dal client (E2EE, S1): il client genera/possiede
    /// la CEK, la wrappa con la chiave pubblica RSA di ogni device destinatario e posta i blob. Usato
    /// alla creazione (provisioning epoch 1), all'add-member (grant) e alla rotazione (remove-member).
    /// </summary>
    Task PostKeysAsync(Guid conversationId, List<WrappedKey> wraps);

    /// <summary>
    /// Registra (o aggiorna) le chiavi pubbliche di questo device nella directory server (E2EE).
    /// Va chiamato all'avvio/login: senza, il device non è nella directory e il server rifiuta i suoi
    /// invii (S4) e nessuno può wrappare la CEK per lui (S1).
    /// </summary>
    Task RegisterDeviceAsync(DeviceRegistration registration);
}

public record TombstoneRecord(Guid MessageId, long Seq, int EpochId);

