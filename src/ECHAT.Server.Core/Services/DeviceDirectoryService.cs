using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Logica della directory di chiavi pubbliche per device (S1-S4). Registrazione legata allo UserId
/// del JWT (mai dal body), lookup per utente/conversazione. Niente EF qui: dipende da
/// <see cref="IDevicePublicKeyStore"/> e <see cref="IMemberStore"/>, quindi è unit-testabile con fake.
/// </summary>
public class DeviceDirectoryService
{
    private readonly IDevicePublicKeyStore _store;
    private readonly IMemberStore _members;
    private readonly ILogger<DeviceDirectoryService> _logger;

    public DeviceDirectoryService(
        IDevicePublicKeyStore store,
        IMemberStore members,
        ILogger<DeviceDirectoryService> logger)
    {
        _store = store;
        _members = members;
        _logger = logger;
    }

    /// <summary>
    /// Registra le chiavi pubbliche del device per <paramref name="userId"/> (preso dal JWT).
    /// Rifiuta un device già posseduto da un altro utente (anti-takeover, ancora di S4).
    /// </summary>
    public async Task RegisterAsync(Guid userId, DeviceRegistration reg)
    {
        if (reg.DeviceId == Guid.Empty)
            throw new ValidationException("DeviceId is required.");
        if (reg.RsaOaepSpki is null || reg.RsaOaepSpki.Length == 0)
            throw new ValidationException("RSA-OAEP public key is required.");
        if (reg.EcdsaSpki is null || reg.EcdsaSpki.Length == 0)
            throw new ValidationException("ECDSA public key is required.");

        var existing = await _store.GetActiveByDeviceAsync(reg.DeviceId);
        if (existing is not null && existing.UserId != userId)
            throw new ForbiddenException(); // un device appartiene a un solo utente

        await _store.UpsertAsync(new DevicePublicKeyRecord(
            userId, reg.DeviceId, reg.RsaOaepSpki, reg.EcdsaSpki, DateTime.UtcNow));

        _logger.LogInformation(
            "Device registered: userId={UserId} deviceId={DeviceId}", userId, reg.DeviceId);
    }

    /// <summary>I device attivi dell'utente autenticato (per capire se questo browser è già enrolled).</summary>
    public async Task<List<DevicePublicKey>> GetMyDevicesAsync(Guid userId)
        => (await _store.GetActiveForUserAsync(userId)).Select(ToDto).ToList();

    /// <summary>
    /// Tutti i device attivi dei membri attivi di una conversazione. I client lo usano per wrappare
    /// la CEK verso ogni device. (L'autorizzazione di accesso è applicata dal filtro sul controller.)
    /// </summary>
    public async Task<List<DevicePublicKey>> GetConversationDevicesAsync(Guid conversationId)
    {
        var memberIds = (await _members.ListActiveWithUserAsync(conversationId))
            .Select(m => m.UserId)
            .Distinct()
            .ToList();
        if (memberIds.Count == 0)
            return new();
        return (await _store.GetActiveForUsersAsync(memberIds)).Select(ToDto).ToList();
    }

    /// <summary>
    /// Le chiavi pubbliche di un singolo device per id. Usato dal ricevente per verificare la firma di
    /// messaggi di mittenti NON più tra i membri attivi (es. rimossi): rimuovere un membro non revoca il
    /// suo device, quindi il record resta risolvibile e i suoi messaggi storici restano verificabili.
    /// Le chiavi pubbliche non sono segrete: l'accesso richiede solo l'autenticazione (filtro sul controller).
    /// </summary>
    public async Task<DevicePublicKey?> GetDeviceAsync(Guid deviceId)
    {
        var rec = await _store.GetActiveByDeviceAsync(deviceId);
        return rec is null ? null : ToDto(rec);
    }

    private static DevicePublicKey ToDto(DevicePublicKeyRecord r) => new()
    {
        UserId = r.UserId,
        DeviceId = r.DeviceId,
        RsaOaepSpki = r.RsaOaepSpki,
        EcdsaSpki = r.EcdsaSpki,
    };
}
