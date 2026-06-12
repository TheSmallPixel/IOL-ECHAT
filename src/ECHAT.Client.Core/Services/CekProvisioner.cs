using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Provisioning client-side delle CEK (E2EE redesign, S1). Il server non genera più chiavi: è il
/// client che possiede la CEK a generarla e a distribuirla wrappandola con la chiave pubblica RSA di
/// ogni device destinatario, poi postando i blob (<see cref="IChatServerGateway.PostKeysAsync"/>).
///
/// Usi:
///  - <see cref="ProvisionEpochAsync"/>: creazione conversazione (epoch 1) o rotazione (remove-member,
///    nuovo epoch); genera una CEK fresca e la distribuisce a tutti i device dei membri attuali.
///  - <see cref="GrantCurrentAsync"/>: add-member; ri-wrappa la CEK CORRENTE per tutti i device
///    (i device del nuovo membro ottengono così il grant; l'upsert server-side evita duplicati).
///
/// Modello attuale: un wrap per utente (DeviceId = userId), cifrato per il device attivo dell'utente.
/// Il multi-device per utente è un follow-up (richiede wrap per (userId, deviceId) fisico).
/// </summary>
public class CekProvisioner
{
    private const byte KeyWrapVersionV1 = 1; // RSA-OAEP v1 (magic 0xB2 sul wire)

    private readonly IChatServerGateway _gateway;
    private readonly IDeviceKeyStore _keyStore;
    private readonly MessageFlowOrchestrator _flow;

    public CekProvisioner(IChatServerGateway gateway, IDeviceKeyStore keyStore, MessageFlowOrchestrator flow)
    {
        _gateway = gateway;
        _keyStore = keyStore;
        _flow = flow;
    }

    /// <summary>
    /// Genera una CEK fresca per <paramref name="epochId"/>, la wrappa per tutti i device dei membri,
    /// la posta e la mette in cache locale. Ritorna la CEK in chiaro (resta solo in memoria).
    /// </summary>
    public async Task<byte[]> ProvisionEpochAsync(Guid conversationId, int epochId)
    {
        var cek = RandomNumberGenerator.GetBytes(32);
        await WrapAndPostAsync(conversationId, epochId, cek);
        _flow.SetCek(conversationId, epochId, cek);
        return cek;
    }

    /// <summary>
    /// Ri-wrappa la CEK dell'epoch corrente per tutti i device dei membri (add-member): i device del
    /// nuovo membro ottengono il grant. Idempotente per i device già in possesso (upsert server-side).
    /// </summary>
    public async Task GrantCurrentAsync(Guid conversationId)
    {
        var (cek, epochId) = await _flow.GetCurrentCekAsync(conversationId);
        await WrapAndPostAsync(conversationId, epochId, cek);
    }

    private async Task WrapAndPostAsync(Guid conversationId, int epochId, byte[] cek)
    {
        // Includiamo SEMPRE noi stessi (dalla nostra coppia locale) così chi provisiona può sempre
        // rileggere, anche se la directory non ci ha ancora propagati; poi aggiungiamo gli altri membri.
        var selfUserId = await _gateway.GetCurrentUserIdAsync();
        var self = await _keyStore.EnsureDeviceAsync();
        var rsaByUser = new Dictionary<Guid, byte[]> { [selfUserId] = self.RsaSpki };

        var devices = await _gateway.GetConversationDevicesAsync(conversationId);
        foreach (var byUser in devices.GroupBy(d => d.UserId))
        {
            if (rsaByUser.ContainsKey(byUser.Key)) continue;
            var device = byUser.First(); // un device per utente (vedi nota sul multi-device)
            if (device.RsaOaepSpki is { Length: > 0 })
                rsaByUser[byUser.Key] = device.RsaOaepSpki;
        }

        var wraps = new List<WrappedKey>(rsaByUser.Count);
        foreach (var (userId, rsaSpki) in rsaByUser)
        {
            var wrappedCek = await _keyStore.WrapCekAsync(cek, rsaSpki);
            wraps.Add(new WrappedKey
            {
                ConversationId = conversationId,
                EpochId = epochId,
                DeviceId = userId,
                WrappedCek = wrappedCek,
                KeyWrapVersion = KeyWrapVersionV1
            });
        }

        if (wraps.Count > 0)
            await _gateway.PostKeysAsync(conversationId, wraps);
    }
}
