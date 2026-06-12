using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Events;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests;

public class MessageModerationServiceTests
{
    private readonly Mock<IMessageRepository> _messages = new();
    private readonly Mock<IMembershipReader> _membership = new();
    private readonly Mock<IAuditLog> _audit = new();
    private readonly Mock<IRealtimeNotifier> _notifier = new();

    private readonly Guid _conv = Guid.NewGuid();

    private MessageModerationService Sut() => new(
        _messages.Object, _membership.Object, _audit.Object, _notifier.Object,
        NullLogger<MessageModerationService>.Instance);

    private void SeedMessage(long seq, Guid senderUserId) =>
        _messages.Setup(m => m.GetBySeqAsync(_conv, seq))
            .ReturnsAsync(new MessageEnvelope { ConversationId = _conv, Seq = seq, MessageId = Guid.NewGuid(), SenderUserId = senderUserId });

    private void Role(Guid userId, string? role) =>
        _membership.Setup(m => m.GetRoleAsync(userId, _conv)).ReturnsAsync(role);

    [Fact]
    public async Task Hide_LowerRankSender_Succeeds_SetsFlag_Audits_Notifies()
    {
        var admin = Guid.NewGuid();
        var member = Guid.NewGuid();
        SeedMessage(5, member);
        Role(admin, "Admin");
        Role(member, "Member");
        _messages.Setup(m => m.SetModerationAsync(_conv, 5, true, admin, "spam")).ReturnsAsync(true);

        var evt = await Sut().ModerateAsync(_conv, admin, 5, hidden: true, reason: "spam");

        evt.Hidden.Should().BeTrue();
        evt.Seq.Should().Be(5);
        evt.ByUserId.Should().Be(admin);
        _messages.Verify(m => m.SetModerationAsync(_conv, 5, true, admin, "spam"), Times.Once);
        _audit.Verify(a => a.RecordAsync(It.Is<AuditEntry>(e => e.Action == "MessageHidden" && e.UserId == admin)), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(_conv, It.IsAny<MessageModeratedEvent>()), Times.Once);
    }

    [Fact]
    public async Task Unhide_RecordsUnhiddenAudit()
    {
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        SeedMessage(7, member);
        Role(owner, "Owner");
        Role(member, "Member");
        _messages.Setup(m => m.SetModerationAsync(_conv, 7, false, owner, null)).ReturnsAsync(true);

        var evt = await Sut().ModerateAsync(_conv, owner, 7, hidden: false, reason: null);

        evt.Hidden.Should().BeFalse();
        _audit.Verify(a => a.RecordAsync(It.Is<AuditEntry>(e => e.Action == "MessageUnhidden")), Times.Once);
    }

    [Fact]
    public async Task Moderator_CannotModerateAdminMessage_Throws_AndDoesNotMutate()
    {
        var moderator = Guid.NewGuid();
        var admin = Guid.NewGuid();
        SeedMessage(3, admin);
        Role(moderator, "Moderator");
        Role(admin, "Admin");

        Func<Task> act = () => Sut().ModerateAsync(_conv, moderator, 3, hidden: true, reason: null);

        await act.Should().ThrowAsync<ForbiddenException>();
        _messages.Verify(m => m.SetModerationAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Moderator_CanModerateAnotherModeratorsMessage()
    {
        var mod1 = Guid.NewGuid();
        var mod2 = Guid.NewGuid();
        SeedMessage(4, mod2);
        Role(mod1, "Moderator");
        Role(mod2, "Moderator");
        _messages.Setup(m => m.SetModerationAsync(_conv, 4, true, mod1, null)).ReturnsAsync(true);

        var evt = await Sut().ModerateAsync(_conv, mod1, 4, hidden: true, reason: null);

        evt.Hidden.Should().BeTrue();
        _messages.Verify(m => m.SetModerationAsync(_conv, 4, true, mod1, null), Times.Once);
    }

    [Fact]
    public async Task SelfModeration_AllowedRegardlessOfRank()
    {
        var me = Guid.NewGuid();
        SeedMessage(9, me);
        Role(me, "Member"); // anche un Member può nascondere il PROPRIO messaggio (permesso a monte a parte)
        _messages.Setup(m => m.SetModerationAsync(_conv, 9, true, me, null)).ReturnsAsync(true);

        var evt = await Sut().ModerateAsync(_conv, me, 9, hidden: true, reason: null);

        evt.Hidden.Should().BeTrue();
        // Nessuna lookup di gerarchia necessaria sul proprio messaggio.
        _membership.Verify(m => m.GetRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task UnknownMessage_ThrowsNotFound()
    {
        _messages.Setup(m => m.GetBySeqAsync(_conv, 99)).ReturnsAsync((MessageEnvelope?)null);

        Func<Task> act = () => Sut().ModerateAsync(_conv, Guid.NewGuid(), 99, hidden: true, reason: null);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
