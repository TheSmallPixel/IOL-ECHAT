namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Cifratura/decifratura del corpo di un file (AES-GCM v1, magic byte 0xA1). In produzione è
/// implementata dall'App via Web Worker (<c>WorkerFileEncryptor</c>); nei test da un fake.
/// Il wire format prodotto è: 0xA1 | IV(12) | (ciphertext || authTag(16)).
/// </summary>
public interface IFileBodyCipher
{
    /// <summary>Magic byte del formato AES-GCM v1.</summary>
    byte AesGcmV1Magic { get; }

    /// <summary>Cifra il payload con la DEK. Restituisce il buffer 0xA1 | IV | ciphertext+tag.</summary>
    Task<byte[]> EncryptAsync(byte[] plaintext, byte[] key);

    /// <summary>Decifra un buffer AES-GCM v1 (il magic byte deve essere già stato validato a monte).</summary>
    Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] key);
}
