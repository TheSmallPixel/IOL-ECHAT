namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Facade crittografica del device (E2EE redesign, S1-S4). Le chiavi private sono CryptoKey
/// NON estraibili in IndexedDB: si possono firmare/unwrappare finché la sessione è viva, ma i byte
/// privati non lasciano mai il browser. Tutte le operazioni passano da WebCrypto (impl in Client.App),
/// quindi sono async. I test C# usano un fake basato su crypto .NET reale (ECDSA P-256 + RSA-OAEP),
/// così la semantica firma/verifica/wrap/unwrap è davvero esercitata, identica alla produzione.
/// </summary>
public interface IDeviceKeyStore
{
    /// <summary>
    /// Garantisce che questo device abbia le sue coppie di chiavi (RSA-OAEP per il wrap della CEK,
    /// ECDSA P-256 per le firme) e sia registrato nella directory; ritorna id device + chiavi pubbliche.
    /// Idempotente: se già enrolled, ritorna lo stato esistente.
    /// </summary>
    Task<DeviceKeys> EnsureDeviceAsync();

    /// <summary>Id stabile di questo device (dopo l'enrollment).</summary>
    Task<Guid> GetDeviceIdAsync();

    /// <summary>Firma un digest a 32 byte (output di EnvelopeHasher) con la ECDSA privata del device  64 byte P1363.</summary>
    Task<byte[]> SignHashAsync(byte[] hash);

    /// <summary>Verifica una firma ECDSA P-256 (P1363) su un digest contro la chiave pubblica SPKI del firmatario.</summary>
    Task<bool> VerifySignatureAsync(byte[] hash, byte[] signature, byte[] signerEcdsaSpki);

    /// <summary>Wrappa una CEK da 32 byte per un destinatario (RSA-OAEP)  <c>0xB2 ‖ wrapped</c>.</summary>
    Task<byte[]> WrapCekAsync(byte[] cek, byte[] recipientRsaSpki);

    /// <summary>Unwrappa un blob <c>0xB2</c> con la RSA privata di questo device  CEK grezza da 32 byte.</summary>
    Task<byte[]> UnwrapCekAsync(byte[] wrappedCek);
}

/// <summary>Identità pubblica del device: id + chiavi pubbliche (SPKI DER) per wrap e firma.</summary>
public record DeviceKeys(Guid DeviceId, byte[] RsaSpki, byte[] EcdsaSpki);
