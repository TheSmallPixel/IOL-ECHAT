using ECHAT.Models.Domain;
using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

public class EnvelopeValidator : IEnvelopeValidator
{
    public const int MaxNonceBytes = 64;
    public const int MaxSignatureBytes = 256;
    public const int MaxLeaseTokenChars = 128;
    public const int MaxCiphertextBytes = 1 * 1024 * 1024; // 1 MiB di ciphertext per envelope

    /// <summary>
    /// Verifica le dimensioni di un <see cref="MessageEnvelope"/> in arrivo.
    /// Ritorna il motivo in caso di errore, null se entro i limiti.
    /// </summary>
    public string? Validate(MessageEnvelope envelope)
    {
        if (envelope.Ciphertext is null) return "Ciphertext is required.";
        if (envelope.Ciphertext.Length == 0) return "Ciphertext cannot be empty.";
        if (envelope.Ciphertext.Length > MaxCiphertextBytes)
            return $"Ciphertext exceeds {MaxCiphertextBytes} bytes.";
        if (envelope.Nonce is { Length: > MaxNonceBytes })
            return $"Nonce exceeds {MaxNonceBytes} bytes.";
        if (envelope.Signature is { Length: > MaxSignatureBytes })
            return $"Signature exceeds {MaxSignatureBytes} bytes.";
        if ((envelope.LeaseToken?.Length ?? 0) > MaxLeaseTokenChars)
            return $"LeaseToken exceeds {MaxLeaseTokenChars} chars.";
        if (envelope.Seq <= 0) return "Seq must be positive.";
        if (envelope.EpochId <= 0) return "EpochId must be positive.";
        return null;
    }
}
