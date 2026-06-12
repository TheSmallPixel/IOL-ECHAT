namespace ECHAT.Models.Dtos;

/// <summary>
/// Inviato dal client al server per registrare le chiavi pubbliche di un device. Il server lega
/// la registrazione allo UserId del JWT (non si fida di alcun UserId nel body): è l'ancora di S4.
/// Le chiavi private corrispondenti non lasciano MAI il browser (CryptoKey non estraibili in IndexedDB).
/// </summary>
public class DeviceRegistration
{
    /// <summary>GUID del device generato dal client (stabile per installazione del browser).</summary>
    public Guid DeviceId { get; init; }

    /// <summary>Chiave pubblica RSA-OAEP-2048 (SPKI DER) usata per ricevere la CEK wrappata.</summary>
    public byte[] RsaOaepSpki { get; init; } = Array.Empty<byte>();

    /// <summary>Chiave pubblica ECDSA P-256 (SPKI DER) usata per verificare le firme dei messaggi.</summary>
    public byte[] EcdsaSpki { get; init; } = Array.Empty<byte>();
}
