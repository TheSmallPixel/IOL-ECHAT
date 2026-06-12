using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Dtos;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

/// <summary>
/// Casi limite del wrapping della CEK in <see cref="CekProvisioner"/> emersi dal mutation testing:
/// un device membro SENZA chiave pubblica RSA (SPKI vuota) deve essere SALTATO (niente wrap prodotto
/// per lui), mentre il device locale è sempre incluso esattamente una volta.
/// </summary>
public class CekProvisionerGapTests
{
    private readonly Guid _conv = Guid.NewGuid();
    private readonly FakeChatServerGateway _gateway = new();
    private readonly FakeDeviceKeyStore _keys = new();
    private readonly FakeCryptoEngine _crypto = new();
    private readonly SequenceLeaseManager _leases = new();
    private readonly MessageFlowOrchestrator _flow;
    private readonly CekProvisioner _sut;

    public CekProvisionerGapTests()
    {
        _flow = new MessageFlowOrchestrator(_gateway, _crypto, _keys, _leases, new ChainValidator(_keys));
        _sut = new CekProvisioner(_gateway, _keys, _flow);
    }

    [Fact]
    public async Task ProvisionEpoch_SkipsMemberWithEmptyRsaKey_ButStillWrapsValidMembers()
    {
        var goodUser = Guid.NewGuid();
        var goodKeys = new FakeDeviceKeyStore();
        var brokenUser = Guid.NewGuid();

        _gateway.Devices.Add(new DevicePublicKey
        {
            UserId = goodUser, DeviceId = goodKeys.DeviceId,
            RsaOaepSpki = goodKeys.RsaSpki, EcdsaSpki = goodKeys.EcdsaSpki
        });
        _gateway.Devices.Add(new DevicePublicKey
        {
            UserId = brokenUser, DeviceId = Guid.NewGuid(),
            RsaOaepSpki = System.Array.Empty<byte>(), EcdsaSpki = System.Array.Empty<byte>() // no RSA key
        });

        await _sut.ProvisionEpochAsync(_conv, 1);

        var wraps = _gateway.PostedKeys.Should().ContainSingle().Subject.wraps;
        var targets = wraps.Select(w => w.DeviceId).ToList();
        targets.Should().Contain(_gateway.CurrentUserId); // self always included...
        targets.Should().Contain(goodUser);               // ...and the member with a real RSA key
        targets.Should().NotContain(brokenUser);          // the keyless member is skipped, not wrapped with garbage
    }

    [Fact]
    public async Task ProvisionEpoch_CachesCekLocally_NoServerFetchNeededAfterwards()
    {
        var cek = await _sut.ProvisionEpochAsync(_conv, 1);
        _gateway.GetKeysCallCount = 0; // reset; a cache hit must not query the server again

        var (current, epoch) = await _flow.GetCurrentCekAsync(_conv);

        epoch.Should().Be(1);
        current.Should().BeEquivalentTo(cek);
        _gateway.GetKeysCallCount.Should().Be(0, "ProvisionEpoch must cache the CEK locally (SetCek), so no server round-trip");
    }
}
