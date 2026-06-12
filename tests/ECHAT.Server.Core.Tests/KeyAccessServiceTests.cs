using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests;

public class KeyAccessServiceTests
{
    private readonly Mock<IKeyEnvelopeStore> _keyStore = new();
    private readonly Mock<IMemberStore> _members = new();

    private KeyAccessService CreateSut()
        => new(_keyStore.Object, _members.Object, NullLogger<KeyAccessService>.Instance);

    private void SeedActiveMembers(Guid conversationId, params Guid[] userIds)
        => _members.Setup(m => m.ListActiveWithUserAsync(conversationId))
            .ReturnsAsync(userIds.Select(u => new MemberWithUser(u, $"{u:N}@x", "U", null, "Member", DateTime.UtcNow)).ToList());

    // Wrap ben formato: 257 byte = 1 magic 0xB2 + 256 byte di ciphertext RSA-OAEP-2048.
    private static byte[] ValidWrap()
    {
        var blob = new byte[257];
        blob[0] = 0xB2;
        return blob;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateDeviceOwnership_OnlyAllowsMatchingDevice(bool sameDevice)
    {
        var userId = Guid.NewGuid();
        var requested = sameDevice ? userId : Guid.NewGuid();

        CreateSut().ValidateDeviceOwnership(userId, requested).Should().Be(sameDevice);
    }

    [Fact]
    public async Task ResolveKeysAsync_NoKeys_ReturnsEmpty_NoServerGeneration()
    {
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _keyStore.Setup(s => s.GetKeysAsync(conversationId, It.IsAny<int?>(), userId))
            .ReturnsAsync(new List<WrappedKey>());

        var result = await CreateSut().ResolveKeysAsync(conversationId, userId, epochId: null, deviceId: null);

        result.Should().BeEmpty();
        _keyStore.Verify(s => s.StoreWrapsAsync(It.IsAny<Guid>(), It.IsAny<List<WrappedKey>>()), Times.Never);
    }

    [Fact]
    public async Task ResolveKeysAsync_KeysExist_ReturnsThem()
    {
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _keyStore.Setup(s => s.GetKeysAsync(conversationId, It.IsAny<int?>(), userId))
            .ReturnsAsync(new List<WrappedKey> { new() { DeviceId = userId } });

        var result = await CreateSut().ResolveKeysAsync(conversationId, userId, epochId: null, deviceId: null);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task ResolveKeysAsync_NullDeviceId_DefaultsToUserId()
    {
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _keyStore.Setup(s => s.GetKeysAsync(conversationId, It.IsAny<int?>(), userId))
            .ReturnsAsync(new List<WrappedKey> { new() { DeviceId = userId } });

        await CreateSut().ResolveKeysAsync(conversationId, userId, epochId: 5, deviceId: null);

        _keyStore.Verify(s => s.GetKeysAsync(conversationId, 5, userId), Times.Once);
    }

    [Fact]
    public async Task StoreClientWrapsAsync_ValidWrapsForMembers_StampsConversationAndStores()
    {
        var conversationId = Guid.NewGuid();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        SeedActiveMembers(conversationId, deviceA, deviceB);
        var wraps = new List<WrappedKey>
        {
            new() { ConversationId = Guid.Empty, EpochId = 1, DeviceId = deviceA, WrappedCek = ValidWrap(), KeyWrapVersion = 1 },
            new() { ConversationId = Guid.Empty, EpochId = 1, DeviceId = deviceB, WrappedCek = ValidWrap(), KeyWrapVersion = 1 },
        };

        List<WrappedKey>? stored = null;
        _keyStore.Setup(s => s.StoreWrapsAsync(conversationId, It.IsAny<List<WrappedKey>>()))
            .Callback<Guid, List<WrappedKey>>((_, w) => stored = w)
            .Returns(Task.CompletedTask);

        await CreateSut().StoreClientWrapsAsync(conversationId, wraps);

        stored.Should().NotBeNull();
        stored!.Should().HaveCount(2);
        stored.Should().OnlyContain(w => w.ConversationId == conversationId); // stamped from route
    }

    [Fact]
    public async Task StoreClientWrapsAsync_Empty_Throws()
    {
        Func<Task> act = () => CreateSut().StoreClientWrapsAsync(Guid.NewGuid(), new List<WrappedKey>());
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task StoreClientWrapsAsync_EmptyTargetDevice_Throws()
    {
        var conv = Guid.NewGuid();
        SeedActiveMembers(conv); // members fetched before the per-wrap checks; empty list is fine
        var wraps = new List<WrappedKey>
        {
            new() { EpochId = 1, DeviceId = Guid.Empty, WrappedCek = ValidWrap(), KeyWrapVersion = 1 },
        };
        Func<Task> act = () => CreateSut().StoreClientWrapsAsync(conv, wraps);
        await act.Should().ThrowAsync<ValidationException>();
        _keyStore.Verify(s => s.StoreWrapsAsync(It.IsAny<Guid>(), It.IsAny<List<WrappedKey>>()), Times.Never);
    }

    [Fact]
    public async Task StoreClientWrapsAsync_TargetNotAMember_ThrowsForbidden()
    {
        var conversationId = Guid.NewGuid();
        SeedActiveMembers(conversationId, Guid.NewGuid()); // some other member, not our target
        var wraps = new List<WrappedKey>
        {
            new() { EpochId = 1, DeviceId = Guid.NewGuid(), WrappedCek = ValidWrap() }, // outsider
        };

        Func<Task> act = () => CreateSut().StoreClientWrapsAsync(conversationId, wraps);
        await act.Should().ThrowAsync<ForbiddenException>();
        _keyStore.Verify(s => s.StoreWrapsAsync(It.IsAny<Guid>(), It.IsAny<List<WrappedKey>>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 257, (byte)0xB2)]   // bad epoch
    [InlineData(1, 10, (byte)0xB2)]    // wrong length
    [InlineData(1, 257, (byte)0x00)]   // wrong magic
    public async Task StoreClientWrapsAsync_MalformedWrap_ThrowsValidation(int epoch, int cekLen, byte magic)
    {
        var conversationId = Guid.NewGuid();
        var device = Guid.NewGuid();
        SeedActiveMembers(conversationId, device); // target IS a member, so we reach structure checks
        var cek = new byte[cekLen];
        if (cekLen > 0) cek[0] = magic;
        var wraps = new List<WrappedKey> { new() { EpochId = epoch, DeviceId = device, WrappedCek = cek, KeyWrapVersion = 1 } };

        Func<Task> act = () => CreateSut().StoreClientWrapsAsync(conversationId, wraps);
        await act.Should().ThrowAsync<ValidationException>();
        _keyStore.Verify(s => s.StoreWrapsAsync(It.IsAny<Guid>(), It.IsAny<List<WrappedKey>>()), Times.Never);
    }

    [Fact]
    public async Task StoreClientWrapsAsync_UnsupportedVersion_ThrowsValidation()
    {
        var conversationId = Guid.NewGuid();
        var device = Guid.NewGuid();
        SeedActiveMembers(conversationId, device);
        var wraps = new List<WrappedKey>
        {
            new() { EpochId = 1, DeviceId = device, WrappedCek = ValidWrap(), KeyWrapVersion = 99 },
        };

        Func<Task> act = () => CreateSut().StoreClientWrapsAsync(conversationId, wraps);
        await act.Should().ThrowAsync<ValidationException>();
        _keyStore.Verify(s => s.StoreWrapsAsync(It.IsAny<Guid>(), It.IsAny<List<WrappedKey>>()), Times.Never);
    }
}
