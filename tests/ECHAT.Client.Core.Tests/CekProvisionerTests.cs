using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Dtos;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class CekProvisionerTests
{
    private readonly Guid _conv = Guid.NewGuid();
    private readonly FakeChatServerGateway _gateway = new();
    private readonly FakeDeviceKeyStore _keys = new();
    private readonly FakeCryptoEngine _crypto = new();
    private readonly SequenceLeaseManager _leases = new();
    private readonly MessageFlowOrchestrator _flow;
    private readonly CekProvisioner _sut;

    public CekProvisionerTests()
    {
        _flow = new MessageFlowOrchestrator(_gateway, _crypto, _keys, _leases, new ChainValidator(_keys));
        _sut = new CekProvisioner(_gateway, _keys, _flow);
    }

    private static DevicePublicKey DeviceOf(Guid userId, FakeDeviceKeyStore keys) => new()
    {
        UserId = userId,
        DeviceId = keys.DeviceId,
        RsaOaepSpki = keys.RsaSpki,
        EcdsaSpki = keys.EcdsaSpki
    };

    [Fact]
    public async Task ProvisionEpoch_PostsWrapForSelf_AndCachesLocally()
    {
        // Directory vuota  solo il wrap per noi stessi (sempre incluso).
        var cek = await _sut.ProvisionEpochAsync(_conv, 1);

        cek.Should().HaveCount(32);
        _gateway.PostedKeys.Should().ContainSingle();
        _gateway.PostedKeys[0].wraps.Should().ContainSingle()
            .Which.DeviceId.Should().Be(_gateway.CurrentUserId);

        // Cache locale: GetCurrentCek torna la stessa CEK senza ripassare dal server.
        var (current, epoch) = await _flow.GetCurrentCekAsync(_conv);
        epoch.Should().Be(1);
        current.Should().BeEquivalentTo(cek);
    }

    [Fact]
    public async Task ProvisionEpoch_WrapsForEveryMemberDevice_RecipientCanUnwrap()
    {
        var otherUser = Guid.NewGuid();
        var otherKeys = new FakeDeviceKeyStore();
        _gateway.Devices.Add(DeviceOf(otherUser, otherKeys));

        var cek = await _sut.ProvisionEpochAsync(_conv, 1);

        var wraps = _gateway.PostedKeys[0].wraps;
        wraps.Select(w => w.DeviceId).Should().BeEquivalentTo(new[] { _gateway.CurrentUserId, otherUser });

        // Il destinatario unwrappa il SUO blob con la sua chiave privata  ottiene la CEK reale.
        var otherWrap = wraps.Single(w => w.DeviceId == otherUser);
        (await otherKeys.UnwrapCekAsync(otherWrap.WrappedCek)).Should().BeEquivalentTo(cek);
    }

    [Fact]
    public async Task ProvisionedWraps_AreRsaWrapped_NotCleartext()
    {
        var cek = await _sut.ProvisionEpochAsync(_conv, 1);
        var wrap = _gateway.PostedKeys[0].wraps.Single();
        wrap.WrappedCek[0].Should().Be(0xB2, "deve essere un wrap RSA-OAEP v1, non la CEK in chiaro");
        wrap.WrappedCek.Should().NotEqual(cek);
        wrap.KeyWrapVersion.Should().Be((byte)1);
    }

    [Fact]
    public async Task GrantCurrent_ReWrapsCurrentCek_ForNewMember()
    {
        var cek = await _sut.ProvisionEpochAsync(_conv, 1);
        _gateway.PostedKeys.Clear();

        var newUser = Guid.NewGuid();
        var newKeys = new FakeDeviceKeyStore();
        _gateway.Devices.Add(DeviceOf(newUser, newKeys));

        await _sut.GrantCurrentAsync(_conv);

        var wraps = _gateway.PostedKeys[0].wraps;
        wraps.Select(w => w.DeviceId).Should().Contain(newUser);
        var newWrap = wraps.Single(w => w.DeviceId == newUser);
        (await newKeys.UnwrapCekAsync(newWrap.WrappedCek)).Should().BeEquivalentTo(cek);
    }
}
