using ECHAT.Models.Domain;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class SenderIdentityHandlerTests
{
    private readonly Mock<IDevicePublicKeyStore> _devices = new();

    private SenderIdentityHandler Sut()
        => new(_devices.Object, NullLogger<SenderIdentityHandler>.Instance);

    private static IngestContext Ctx(Guid authUser, Guid senderUser, Guid deviceId)
        => new(new MessageEnvelope { SenderUserId = senderUser, SenderDeviceId = deviceId }) { UserId = authUser };

    [Fact]
    public async Task OwnDeviceAndMatchingUser_CallsNext()
    {
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        _devices.Setup(d => d.GetActiveByDeviceAsync(device))
            .ReturnsAsync(new DevicePublicKeyRecord(user, device, new byte[] { 1 }, new byte[] { 2 }, DateTime.UtcNow));
        var nextCalled = false;

        await Sut().HandleAsync(Ctx(user, user, device), () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task SenderUserIdMismatch_ThrowsForbidden_AndDoesNotCallNext()
    {
        var auth = Guid.NewGuid();
        var spoofed = Guid.NewGuid();
        var nextCalled = false;

        Func<Task> act = () => Sut().HandleAsync(
            Ctx(auth, spoofed, Guid.NewGuid()), () => { nextCalled = true; return Task.CompletedTask; });

        await act.Should().ThrowAsync<ForbiddenException>();
        nextCalled.Should().BeFalse();
        _devices.Verify(d => d.GetActiveByDeviceAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task UnknownDevice_ThrowsForbidden()
    {
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        _devices.Setup(d => d.GetActiveByDeviceAsync(device)).ReturnsAsync((DevicePublicKeyRecord?)null);

        Func<Task> act = () => Sut().HandleAsync(Ctx(user, user, device), () => Task.CompletedTask);
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task DeviceOwnedByAnotherUser_ThrowsForbidden()
    {
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var device = Guid.NewGuid();
        _devices.Setup(d => d.GetActiveByDeviceAsync(device))
            .ReturnsAsync(new DevicePublicKeyRecord(other, device, new byte[] { 1 }, new byte[] { 2 }, DateTime.UtcNow));

        Func<Task> act = () => Sut().HandleAsync(Ctx(user, user, device), () => Task.CompletedTask);
        await act.Should().ThrowAsync<ForbiddenException>();
    }
}
