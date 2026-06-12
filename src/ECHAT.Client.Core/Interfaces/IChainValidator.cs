using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Interfaces;

public interface IChainValidator
{
    /// <summary>
    /// Valida un envelope ricevuto su quattro assi: <c>SeqValid</c> (seq del payload == seq envelope),
    /// <c>ChainValid</c> (prevEnvelopeHash == hash dell'envelope precedente), <c>DecryptionValid</c>
    /// (<paramref name="decryptionSucceeded"/>: il MAC AES-GCM ha tenuto), <c>MacValid</c> (la firma
    /// ECDSA del mittente verifica contro <paramref name="senderEcdsaSpki"/>). Se la SPKI del mittente
    /// non è disponibile o la firma manca, <c>MacValid</c> è false (fail-closed).
    /// </summary>
    Task<ChainValidationResult> ValidateAsync(
        MessageEnvelope envelope,
        MessagePayload payload,
        byte[] lastEnvelopeHash,
        byte[]? senderEcdsaSpki,
        bool decryptionSucceeded);
}
