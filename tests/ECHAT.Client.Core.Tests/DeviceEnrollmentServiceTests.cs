using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class DeviceEnrollmentServiceTests
{
    private readonly FakeChatServerGateway _gateway = new();
    private readonly FakeDeviceKeyStore _keys = new();

    [Fact]
    public async Task EnsureRegistered_RegistersThisDevicesPublicKeys()
    {
        var sut = new DeviceEnrollmentService(_gateway, _keys);

        await sut.EnsureRegisteredAsync();

        _gateway.RegisteredDevices.Should().ContainSingle();
        var reg = _gateway.RegisteredDevices[0];
        reg.DeviceId.Should().Be(_keys.DeviceId);
        reg.RsaOaepSpki.Should().BeEquivalentTo(_keys.RsaSpki);
        reg.EcdsaSpki.Should().BeEquivalentTo(_keys.EcdsaSpki);
        // Solo chiavi PUBBLICHE registrate (SPKI): nessun byte privato lascia il device.
        reg.RsaOaepSpki.Should().NotBeEmpty();
        reg.EcdsaSpki.Should().NotBeEmpty();
    }
}
