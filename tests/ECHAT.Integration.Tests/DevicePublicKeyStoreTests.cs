using ECHAT.Server.App.Data;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Interfaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

/// <summary>
/// Test della directory delle chiavi pubbliche per device (S1-S4) sullo store EF. È la radice di
/// fiducia TOFU: verifichiamo lookup attivi, esclusione dei device revocati, e la ROTAZIONE
/// (re-upsert) che deve revocare la riga attiva precedente e lasciarne una sola attiva.
/// </summary>
public class DevicePublicKeyStoreTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly DevicePublicKeyStore _sut;

    public DevicePublicKeyStoreTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _sut = new DevicePublicKeyStore(_db);
    }

    public void Dispose() => _db.Dispose();

    private static DevicePublicKeyRecord Rec(Guid user, Guid device, byte rsaSeed = 1, byte ecdsaSeed = 2)
        => new(user, device, new[] { rsaSeed }, new[] { ecdsaSeed }, DateTime.UtcNow);

    [Fact]
    public async Task Upsert_ThenGetActiveByDevice_ReturnsTheKeys()
    {
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        await _sut.UpsertAsync(Rec(user, device, rsaSeed: 0xAA, ecdsaSeed: 0xBB));

        var got = await _sut.GetActiveByDeviceAsync(device);

        got.Should().NotBeNull();
        got!.UserId.Should().Be(user);
        got.DeviceId.Should().Be(device);
        got.RsaOaepSpki.Should().Equal(0xAA);
        got.EcdsaSpki.Should().Equal(0xBB);
    }

    [Fact]
    public async Task GetActiveByDevice_UnknownDevice_ReturnsNull()
        => (await _sut.GetActiveByDeviceAsync(Guid.NewGuid())).Should().BeNull();

    [Fact]
    public async Task Reupsert_RotatesKeys_RevokesOld_KeepsExactlyOneActive()
    {
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        await _sut.UpsertAsync(Rec(user, device, rsaSeed: 0x01));
        await _sut.UpsertAsync(Rec(user, device, rsaSeed: 0x02)); // rotation: new keypair, same device

        // The active lookup returns the NEW keys...
        var active = await _sut.GetActiveByDeviceAsync(device);
        active!.RsaOaepSpki.Should().Equal(0x02);

        // ...and exactly one row is active (the old one is soft-revoked, not deleted).
        var rows = await _db.DevicePublicKeys.Where(x => x.DeviceId == device).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Count(r => r.RevokedAt == null).Should().Be(1);
        rows.Single(r => r.RevokedAt != null).RsaOaepSpki.Should().Equal(0x01); // old one revoked
    }

    [Fact]
    public async Task GetActiveForUser_ReEnrollWithNewDeviceId_RevokesPrior_OneActivePerUser()
    {
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        await _sut.UpsertAsync(Rec(user, d1));
        await _sut.UpsertAsync(Rec(other, Guid.NewGuid())); // a different user's device stays untouched
        // Re-enrolling the SAME user with a NEW deviceId (es. IndexedDB azzerato  nuovo random UUID)
        // revoca il device precedente: il modello one-device-per-user tiene esattamente una riga attiva
        // per utente, così il wrap della CEK non può finire su una chiave morta.
        await _sut.UpsertAsync(Rec(user, d2));

        var devices = await _sut.GetActiveForUserAsync(user);

        devices.Should().ContainSingle().Which.DeviceId.Should().Be(d2);
        // d1 è soft-revoked, non cancellato (storia auditabile).
        (await _db.DevicePublicKeys.SingleAsync(x => x.DeviceId == d1)).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetActiveForUsers_DedupsAndReturnsActiveAcrossUsers()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await _sut.UpsertAsync(Rec(u1, Guid.NewGuid()));
        await _sut.UpsertAsync(Rec(u2, Guid.NewGuid()));

        var devices = await _sut.GetActiveForUsersAsync(new[] { u1, u2, u1 }); // duplicate u1

        devices.Select(d => d.UserId).Should().BeEquivalentTo(new[] { u1, u2 });
    }

    [Fact]
    public async Task GetActiveForUsers_EmptyOrUnknown_ReturnsEmpty()
    {
        (await _sut.GetActiveForUsersAsync(Array.Empty<Guid>())).Should().BeEmpty();
        (await _sut.GetActiveForUsersAsync(new[] { Guid.NewGuid() })).Should().BeEmpty();
    }
}
