using System.Text;
using System.Text.Json;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Engine dei messaggi costruito sui primitivi sostituibili della suite:
/// <see cref="IAeadCipher"/> (AES-GCM), <see cref="ICompressor"/> (gzip), <see cref="ISigner"/> (HMAC).
/// In produzione cipher e signer sono backed-by-WebCrypto (browser), quindi le operazioni sono async.
///
/// L'hash dell'envelope resta sincrono e in C# (<see cref="EnvelopeHasher"/>): è SHA-256 e va
/// calcolato byte-identico anche lato server .NET, che non ha WebCrypto.
/// </summary>
public class AeadCryptoEngine : ICryptoEngine
{
    private readonly IAeadCipher _aead;
    private readonly ICompressor _compressor;
    private readonly ISigner _signer;

    public AeadCryptoEngine(IAeadCipher aead, ICompressor compressor, ISigner signer)
    {
        _aead = aead;
        _compressor = compressor;
        _signer = signer;
    }

    public async Task<EncryptResult> EncryptAsync(
        MessagePayload payload, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var compressed = _compressor.Compress(json);

        var aad = BuildAad(conversationId, messageId, seq, epochId);
        var (ciphertext, nonce) = await _aead.EncryptAsync(compressed, cek, aad);

        return new EncryptResult { Ciphertext = ciphertext, Nonce = nonce };
    }

    public async Task<MessagePayload> DecryptAsync(
        byte[] ciphertext, byte[] nonce, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId)
    {
        var aad = BuildAad(conversationId, messageId, seq, epochId);
        var compressed = await _aead.DecryptAsync(ciphertext, nonce, cek, aad);
        var json = _compressor.Decompress(compressed);
        return JsonSerializer.Deserialize<MessagePayload>(json)!;
    }

    public Task<byte[]> SignAsync(byte[] data, byte[] privateKey) => _signer.SignAsync(data, privateKey);

    public Task<bool> VerifyAsync(byte[] data, byte[] signature, byte[] publicKey) => _signer.VerifyAsync(data, signature, publicKey);

    public byte[] ComputeEnvelopeHash(MessageEnvelope envelope) => EnvelopeHasher.Compute(envelope);

    // Lega il ciphertext al suo contesto: stessa stringa AAD dell'engine storico, così la
    // semantica (e i test di tamper su conv/msg/seq/epoch) resta invariata.
    private static byte[] BuildAad(Guid conversationId, Guid messageId, long seq, int epochId)
        => Encoding.UTF8.GetBytes($"{conversationId}|{messageId}|{seq}|{epochId}");
}
