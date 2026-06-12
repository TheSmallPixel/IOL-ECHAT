using ECHAT.Models.Domain;
using ECHAT.Server.Core.Services;

namespace ECHAT.Server.App.Validation;

/// <summary>
/// Shim retrocompatibile: le regole di validazione vivono ora in
/// <see cref="EnvelopeValidator"/> (Server.Core) per essere testabili a unità.
/// Questo tipo statico delega all'implementazione di Core e ne riespone le costanti.
/// </summary>
public static class EnvelopeLimits
{
    public const int MaxNonceBytes = EnvelopeValidator.MaxNonceBytes;
    public const int MaxSignatureBytes = EnvelopeValidator.MaxSignatureBytes;
    public const int MaxLeaseTokenChars = EnvelopeValidator.MaxLeaseTokenChars;
    public const int MaxCiphertextBytes = EnvelopeValidator.MaxCiphertextBytes;

    private static readonly EnvelopeValidator Validator = new();

    public static string? Validate(MessageEnvelope envelope) => Validator.Validate(envelope);
}
