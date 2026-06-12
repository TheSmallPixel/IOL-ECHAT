using ECHAT.Models.Enums;

namespace ECHAT.Models.Dtos;

public class ChainValidationResult
{
    public bool IsValid { get; init; }
    public bool SeqValid { get; init; }
    public bool ChainValid { get; init; }
    public bool DecryptionValid { get; init; }
    public bool MacValid { get; init; }
    public byte[] CurrentEnvelopeHash { get; init; } = Array.Empty<byte>();
    public ChainError? Error { get; init; }
}
