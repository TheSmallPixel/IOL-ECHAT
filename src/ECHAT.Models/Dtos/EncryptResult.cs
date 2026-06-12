namespace ECHAT.Models.Dtos;

public class EncryptResult
{
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
}
