namespace ECHAT.Models.Dtos;

/// <summary>
/// Voce della directory di chiavi pubbliche restituita dal server: mappa un device a un utente e
/// alle sue chiavi pubbliche. I client la usano per wrappare la CEK verso i device dei membri
/// (RSA) e per verificare le firme dei messaggi (ECDSA). Trust model: directory asserita dal server
/// con pinning TOFU lato client (vedi nota sui limiti residui nella documentazione del redesign).
/// </summary>
public class DevicePublicKey
{
    public Guid UserId { get; init; }
    public Guid DeviceId { get; init; }
    public byte[] RsaOaepSpki { get; init; } = Array.Empty<byte>();
    public byte[] EcdsaSpki { get; init; } = Array.Empty<byte>();
}
