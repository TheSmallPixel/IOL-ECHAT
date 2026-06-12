namespace ECHAT.Server.App.Data.Entities;

/// <summary>
/// Directory delle chiavi pubbliche per device. <see cref="UserId"/> è impostato dal server dal JWT
/// al momento della registrazione (ancora di S4: lega il device a un utente). Le righe sono
/// soft-revoked (<see cref="RevokedAt"/>) per la rotazione, mai mutate, così la storia resta auditabile.
/// </summary>
public class DevicePublicKeyEntity
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public byte[] RsaOaepSpki { get; set; } = Array.Empty<byte>();
    public byte[] EcdsaSpki { get; set; } = Array.Empty<byte>();
    public DateTime RegisteredAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
