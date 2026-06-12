using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Branches of <see cref="DeviceDirectoryService"/> not covered by DeviceDirectoryServiceTests:
/// the empty-membership short-circuit in GetConversationDevices, the de-dup of member ids, and
/// GetMyDevices mapping.
/// </summary>
public class DeviceDirectoryServiceEdgeTests
{
    private readonly Mock<IDevicePublicKeyStore> _store = new();
    private readonly Mock<IMemberStore> _members = new();

    private DeviceDirectoryService Sut()
        => new(_store.Object, _members.Object, NullLogger<DeviceDirectoryService>.Instance);

    [Fact]
    public async Task GetConversationDevices_NoActiveMembers_ReturnsEmpty_WithoutQueryingKeyStore()
    {
        var conv = Guid.NewGuid();
        _members.Setup(m => m.ListActiveWithUserAsync(conv)).ReturnsAsync(new List<MemberWithUser>());

        var devices = await Sut().GetConversationDevicesAsync(conv);

        devices.Should().BeEmpty();
        // Early return must avoid a pointless key-store lookup on an empty id list.
        _store.Verify(s => s.GetActiveForUsersAsync(It.IsAny<IEnumerable<Guid>>()), Times.Never);
    }

    [Fact]
    public async Task GetConversationDevices_DeduplicatesMemberIds_BeforeKeyLookup()
    {
        var conv = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        // Same user appears twice (e.g. two membership rows): the key lookup must receive one id.
        _members.Setup(m => m.ListActiveWithUserAsync(conv)).ReturnsAsync(new List<MemberWithUser>
        {
            new(u1, "a@x", "A", null, "Owner", DateTime.UtcNow),
            new(u1, "a@x", "A", null, "Owner", DateTime.UtcNow),
        });

        List<Guid>? captured = null;
        _store.Setup(s => s.GetActiveForUsersAsync(It.IsAny<IEnumerable<Guid>>()))
            .Callback<IEnumerable<Guid>>(ids => captured = ids.ToList())
            .ReturnsAsync(new List<DevicePublicKeyRecord>
            {
                new(u1, Guid.NewGuid(), new byte[] { 1 }, new byte[] { 2 }, DateTime.UtcNow),
            });

        var devices = await Sut().GetConversationDevicesAsync(conv);

        captured.Should().ContainSingle().Which.Should().Be(u1);
        devices.Should().ContainSingle().Which.UserId.Should().Be(u1);
    }

    [Fact]
    public async Task GetMyDevices_MapsActiveRecordsToDtos()
    {
        var userId = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        _store.Setup(s => s.GetActiveForUserAsync(userId)).ReturnsAsync(new List<DevicePublicKeyRecord>
        {
            new(userId, d1, new byte[] { 1 }, new byte[] { 2 }, DateTime.UtcNow),
            new(userId, d2, new byte[] { 3 }, new byte[] { 4 }, DateTime.UtcNow),
        });

        var devices = await Sut().GetMyDevicesAsync(userId);

        devices.Should().HaveCount(2);
        devices.Select(d => d.DeviceId).Should().BeEquivalentTo(new[] { d1, d2 });
        devices.Should().OnlyContain(d => d.UserId == userId);
    }

    [Fact]
    public async Task GetMyDevices_NoDevices_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetActiveForUserAsync(userId)).ReturnsAsync(new List<DevicePublicKeyRecord>());

        (await Sut().GetMyDevicesAsync(userId)).Should().BeEmpty();
    }
}
