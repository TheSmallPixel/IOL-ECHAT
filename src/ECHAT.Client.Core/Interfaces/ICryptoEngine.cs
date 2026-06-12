using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Interfaces;

public interface ICryptoEngine
{
    // Encrypt/Decrypt/Sign/Verify sono async perché in produzione (browser) girano su WebCrypto,
    // che è async-only. ComputeEnvelopeHash resta sincrono: è SHA-256 (WASM-safe) e soprattutto
    // è condiviso col server .NET (vedi EnvelopeHasher), quindi non può vivere solo lato JS.
    Task<EncryptResult> EncryptAsync(MessagePayload payload, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId);
    Task<MessagePayload> DecryptAsync(byte[] ciphertext, byte[] nonce, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId);
    Task<byte[]> SignAsync(byte[] data, byte[] privateKey);
    Task<bool> VerifyAsync(byte[] data, byte[] signature, byte[] publicKey);
    byte[] ComputeEnvelopeHash(MessageEnvelope envelope);
}
