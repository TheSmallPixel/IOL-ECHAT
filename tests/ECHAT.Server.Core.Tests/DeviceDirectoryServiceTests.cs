using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests;

public class DeviceDirectoryServiceTests
{
    private readonly Mock<IDevicePublicKeyStore> _store = new();
    private readonly Mock<IMemberStore> _members = new();

    private DeviceDirectoryService Sut()
        => new(_store.Object, _members.Object, NullLogger<DeviceDirectoryService>.Instance);

    private static DeviceRegistration ValidReg(Guid? deviceId = null) => new()
    {
        DeviceId = deviceId ?? Guid.NewGuid(),
        RsaOaepSpki = new byte[] { 1, 2, 3 },
        EcdsaSpki = new byte[] { 4, 5, 6 },
    };

    [Fact]
    public async Task Register_BindsTheAuthenticatedUserId_NotAnythingFromTheBody()
    {
        var userId = Guid.NewGuid();
        var reg = ValidReg();
        _store.Setup(s => s.GetActiveByDeviceAsync(reg.DeviceId)).ReturnsAsync((DevicePublicKeyRecord?)null);

        await Sut().RegisterAsync(userId, reg);

        _store.Verify(s => s.UpsertAsync(It.Is<DevicePublicKeyRecord>(
            r => r.UserId == userId && r.DeviceId == reg.DeviceId
                 && r.RsaOaepSpki == reg.RsaOaepSpki && r.EcdsaSpki == reg.EcdsaSpki)), Times.Once);
    }

    [Fact]
    public async Task Register_DeviceOwnedByAnotherUser_ThrowsForbidden()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var reg = ValidReg();
        _store.Setup(s => s.GetActiveByDeviceAsync(reg.DeviceId))
            .ReturnsAsync(new DevicePublicKeyRecord(other, reg.DeviceId, new byte[] { 9 }, new byte[] { 9 }, DateTime.UtcNow));

        Func<Task> act = () => Sut().RegisterAsync(me, reg);

        await act.Should().ThrowAsync<ForbiddenException>();
        _store.Verify(s => s.UpsertAsync(It.IsAny<DevicePublicKeyRecord>()), Times.Never);
    }

    [Fact]
    public async Task Register_ReRegisteringOwnDevice_Allowed()
    {
        var me = Guid.NewGuid();
        var reg = ValidReg();
        _store.Setup(s => s.GetActiveByDeviceAsync(reg.DeviceId))
            .ReturnsAsync(new DevicePublicKeyRecord(me, reg.DeviceId, new byte[] { 9 }, new byte[] { 9 }, DateTime.UtcNow));

        await Sut().RegisterAsync(me, reg); // rotation of my own keys

        _store.Verify(s => s.UpsertAsync(It.Is<DevicePublicKeyRecord>(r => r.UserId == me)), Times.Once);
    }

    [Theory]
    [InlineData(true, false, false)]  // empty deviceId
    [InlineData(false, true, false)]  // empty rsa
    [InlineData(false, false, true)]  // empty ecdsa
    public async Task Register_InvalidInput_ThrowsValidation(bool emptyDevice, bool emptyRsa, bool emptyEcdsa)
    {
        var reg = new DeviceRegistration
        {
            DeviceId = emptyDevice ? Guid.Empty : Guid.NewGuid(),
            RsaOaepSpki = emptyRsa ? Array.Empty<byte>() : new byte[] { 1 },
            EcdsaSpki = emptyEcdsa ? Array.Empty<byte>() : new byte[] { 1 },
        };

        Func<Task> act = () => Sut().RegisterAsync(Guid.NewGuid(), reg);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task GetDevice_ReturnsDeviceByIdForHistoricalVerification()
    {
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _store.Setup(s => s.GetActiveByDeviceAsync(deviceId))
            .ReturnsAsync(new DevicePublicKeyRecord(userId, deviceId, new byte[] { 1 }, new byte[] { 2 }, DateTime.UtcNow));

        var dto = await Sut().GetDeviceAsync(deviceId);

        dto.Should().NotBeNull();
        dto!.DeviceId.Should().Be(deviceId);
        dto.EcdsaSpki.Should().BeEquivalentTo(new byte[] { 2 });
    }

    [Fact]
    public async Task GetDevice_UnknownId_ReturnsNull()
    {
        _store.Setup(s => s.GetActiveByDeviceAsync(It.IsAny<Guid>())).ReturnsAsync((DevicePublicKeyRecord?)null);
        (await Sut().GetDeviceAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task GetConversationDevices_ReturnsActiveDevicesOfAllMembers()
    {
        var conv = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        _members.Setup(m => m.ListActiveWithUserAsync(conv)).ReturnsAsync(new List<MemberWithUser>
        {
            new(u1, "a@x", "A", null, "Owner", DateTime.UtcNow),
            new(u2, "b@x", "B", null, "Member", DateTime.UtcNow),
        });
        _store.Setup(s => s.GetActiveForUsersAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<DevicePublicKeyRecord>
            {
                new(u1, Guid.NewGuid(), new byte[] { 1 }, new byte[] { 2 }, DateTime.UtcNow),
                new(u2, Guid.NewGuid(), new byte[] { 3 }, new byte[] { 4 }, DateTime.UtcNow),
            });

        var devices = await Sut().GetConversationDevicesAsync(conv);

        devices.Should().HaveCount(2);
        devices.Select(d => d.UserId).Should().BeEquivalentTo(new[] { u1, u2 });
    }
}
