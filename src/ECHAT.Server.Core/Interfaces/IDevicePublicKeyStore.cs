namespace ECHAT.Server.Core.Interfaces;

/// <summary>
/// Persistenza della directory di chiavi pubbliche per device (S1-S4). L'implementazione concreta
/// (EF) vive in Server.App; la logica di registrazione/lookup vive in Server.Core
/// (<see cref="Services.DeviceDirectoryService"/>) così resta testabile senza DB.
/// </summary>
public interface IDevicePublicKeyStore
{
    /// <summary>Chiavi attive (non revocate) di un device, o null se assente/revocato.</summary>
    Task<DevicePublicKeyRecord?> GetActiveByDeviceAsync(Guid deviceId);

    /// <summary>Tutti i device attivi di un utente.</summary>
    Task<List<DevicePublicKeyRecord>> GetActiveForUserAsync(Guid userId);

    /// <summary>Tutti i device attivi per un insieme di utenti (es. i membri di una conversazione).</summary>
    Task<List<DevicePublicKeyRecord>> GetActiveForUsersAsync(IEnumerable<Guid> userIds);

    /// <summary>
    /// Registra/ri-registra (rotazione) le chiavi di un device: revoca TUTTE le righe attive
    /// precedenti dell'utente (modello one-device-per-user) e inserisce la nuova, così resta
    /// esattamente una riga attiva per utente: la corrente. Evita che un reset di IndexedDB lato
    /// client (nuovo DeviceId casuale) lasci chiavi attive orfane per cui la CEK verrebbe wrappata.
    /// </summary>
    Task UpsertAsync(DevicePublicKeyRecord record);
}

public record DevicePublicKeyRecord(
    Guid UserId,
    Guid DeviceId,
    byte[] RsaOaepSpki,
    byte[] EcdsaSpki,
    DateTime RegisteredAt);
