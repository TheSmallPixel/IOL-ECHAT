using ECHAT.Client.Core.Commands;
using ECHAT.Client.Core.Services;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class OutboxServiceTests
{
    private OutboxService CreateService() => new();

    private static SendMessageCommand CreateCommand(Guid? messageId = null) => new()
    {
        MessageId = messageId ?? Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        Payload = new MessagePayload { Seq = 1, Text = "test" },
        State = OutboxState.Pending
    };

    [Fact]
    public async Task EnqueueAsync_ShouldAddPendingItem()
    {
        var service = CreateService();
        var cmd = CreateCommand();

        await service.EnqueueAsync(cmd);
        var pending = await service.GetPendingAsync();

        pending.Should().ContainSingle();
        pending[0].MessageId.Should().Be(cmd.MessageId);
        pending[0].State.Should().Be(OutboxState.Pending);
    }

    [Fact]
    public async Task AckAsync_ShouldMarkAsAcked()
    {
        var service = CreateService();
        var cmd = CreateCommand();

        await service.EnqueueAsync(cmd);
        await service.AckAsync(cmd.MessageId);

        var pending = await service.GetPendingAsync();
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task FailAsync_ShouldMarkAsFailed_AndIncrementRetry()
    {
        var service = CreateService();
        var cmd = CreateCommand();

        await service.EnqueueAsync(cmd);
        await service.FailAsync(cmd.MessageId, "network error");

        var pending = await service.GetPendingAsync();
        pending.Should().ContainSingle();
        pending[0].State.Should().Be(OutboxState.Failed);
        pending[0].FailureReason.Should().Be("network error");
        pending[0].RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetPendingAsync_ShouldReturnBothPendingAndFailed()
    {
        var service = CreateService();
        var cmd1 = CreateCommand();
        var cmd2 = CreateCommand();

        await service.EnqueueAsync(cmd1);
        await service.EnqueueAsync(cmd2);
        await service.FailAsync(cmd2.MessageId, "timeout");

        var pending = await service.GetPendingAsync();
        pending.Should().HaveCount(2);
    }

    [Fact]
    public async Task AckAsync_NonExistentMessage_ShouldNotThrow()
    {
        var service = CreateService();
        var act = async () => await service.AckAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }
}
