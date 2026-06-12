using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Validazione lato ricevente (S3). MacValid è ora una VERA verifica di firma ECDSA P-256 sul digest
/// di <see cref="EnvelopeHasher"/> contro la chiave pubblica del device mittente (presa dalla directory),
/// non più una costante. DecryptionValid riflette l'esito reale del MAC AES-GCM (passato dal chiamante).
/// </summary>
public class ChainValidator : IChainValidator
{
    private readonly IDeviceKeyStore _keys;

    public ChainValidator(IDeviceKeyStore keys)
    {
        _keys = keys;
    }

    public async Task<ChainValidationResult> ValidateAsync(
        MessageEnvelope envelope,
        MessagePayload payload,
        byte[] lastEnvelopeHash,
        byte[]? senderEcdsaSpki,
        bool decryptionSucceeded)
    {
        var seqValid = payload.Seq == envelope.Seq;
        var chainValid = lastEnvelopeHash.Length == 0 ||
                         payload.PrevEnvelopeHash.SequenceEqual(lastEnvelopeHash);

        // SHA-256 sull'envelope: WASM-safe e identico al calcolo server (EnvelopeHasher). È anche il
        // pre-image firmato dal mittente, quindi è ciò contro cui verifichiamo la firma.
        var currentHash = EnvelopeHasher.Compute(envelope);

        var macValid = senderEcdsaSpki is { Length: > 0 }
                       && envelope.Signature.Length > 0
                       && await _keys.VerifySignatureAsync(currentHash, envelope.Signature, senderEcdsaSpki);

        var decryptionValid = decryptionSucceeded;

        ChainError? error =
            !seqValid ? ChainError.SeqMismatch
            : !chainValid ? ChainError.HashMismatch
            : !macValid ? ChainError.SignatureInvalid
            : null;

        return new ChainValidationResult
        {
            IsValid = seqValid && chainValid && decryptionValid && macValid,
            SeqValid = seqValid,
            ChainValid = chainValid,
            DecryptionValid = decryptionValid,
            MacValid = macValid,
            CurrentEnvelopeHash = currentHash,
            Error = error
        };
    }
}
