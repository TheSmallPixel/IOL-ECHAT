using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

/// <summary>Implementazione EF di <see cref="IDevicePublicKeyStore"/> sulla tabella DevicePublicKeys.</summary>
public class DevicePublicKeyStore : IDevicePublicKeyStore
{
    private readonly EchatDbContext _db;

    public DevicePublicKeyStore(EchatDbContext db)
    {
        _db = db;
    }

    public async Task<DevicePublicKeyRecord?> GetActiveByDeviceAsync(Guid deviceId)
    {
        var e = await _db.DevicePublicKeys.AsNoTracking()
            .FirstOrDefaultAsync(x => x.DeviceId == deviceId && x.RevokedAt == null);
        return e is null ? null : ToRecord(e);
    }

    public async Task<List<DevicePublicKeyRecord>> GetActiveForUserAsync(Guid userId)
        => (await _db.DevicePublicKeys.AsNoTracking()
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync())
            .Select(ToRecord).ToList();

    public async Task<List<DevicePublicKeyRecord>> GetActiveForUsersAsync(IEnumerable<Guid> userIds)
    {
        var ids = userIds.Distinct().ToList();
        return (await _db.DevicePublicKeys.AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.RevokedAt == null)
                .ToListAsync())
            .Select(ToRecord).ToList();
    }

    public async Task UpsertAsync(DevicePublicKeyRecord record)
    {
        // Modello one-device-per-user (il wrap della CEK è keyed su DeviceId == userId): una nuova
        // registrazione revoca TUTTE le righe attive precedenti dell'UTENTE, non solo quelle con lo
        // stesso DeviceId. echat.js conia un DeviceId casuale nuovo ad ogni reset di IndexedDB
        // (vedi generateRecord), quindi revocare solo per-device lascerebbe righe attive orfane con
        // chiavi ormai morte; il provisioning (CekProvisioner.WrapAndPostAsync  byUser.First()) ne
        // pescherebbe una a caso e wrapperebbe la CEK per una chiave non più unwrappabile: il bug
        // "CEK unwrap failed". Revocando per-utente resta SEMPRE esattamente una riga attiva per
        // utente: la corrente. (Il multi-device reale è un follow-up che cambierà anche il wrap model.)
        var now = DateTime.UtcNow;
        var existing = await _db.DevicePublicKeys
            .Where(x => x.UserId == record.UserId && x.RevokedAt == null)
            .ToListAsync();
        foreach (var e in existing)
            e.RevokedAt = now;

        _db.DevicePublicKeys.Add(new DevicePublicKeyEntity
        {
            UserId = record.UserId,
            DeviceId = record.DeviceId,
            RsaOaepSpki = record.RsaOaepSpki,
            EcdsaSpki = record.EcdsaSpki,
            RegisteredAt = record.RegisteredAt,
        });
        await _db.SaveChangesAsync();
    }

    private static DevicePublicKeyRecord ToRecord(DevicePublicKeyEntity e)
        => new(e.UserId, e.DeviceId, e.RsaOaepSpki, e.EcdsaSpki, e.RegisteredAt);
}
