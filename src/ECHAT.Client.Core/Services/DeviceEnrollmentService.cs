using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Enrollment del device (E2EE redesign, S2/S4). All'avvio/login genera (se assenti) le coppie di
/// chiavi del device e ne registra le chiavi PUBBLICHE nella directory server. È un prerequisito di
/// tutto il resto: senza, il server rifiuta gli invii del device (SenderIdentityHandler) e nessuno
/// può wrappare la CEK per lui. La registrazione è idempotente lato server (upsert per DeviceId).
/// </summary>
public class DeviceEnrollmentService
{
    private readonly IChatServerGateway _gateway;
    private readonly IDeviceKeyStore _keyStore;

    public DeviceEnrollmentService(IChatServerGateway gateway, IDeviceKeyStore keyStore)
    {
        _gateway = gateway;
        _keyStore = keyStore;
    }

    /// <summary>Genera/recupera le chiavi del device e registra le pubbliche nella directory.</summary>
    public async Task EnsureRegisteredAsync()
    {
        var keys = await _keyStore.EnsureDeviceAsync();
        await _gateway.RegisterDeviceAsync(new DeviceRegistration
        {
            DeviceId = keys.DeviceId,
            RsaOaepSpki = keys.RsaSpki,
            EcdsaSpki = keys.EcdsaSpki
        });
    }
}
