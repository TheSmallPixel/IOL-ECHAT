using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests;

public class PolicyEnforcerTests
{
    private readonly Mock<IMembershipReader> _membership = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();

    private PolicyEnforcer Sut() => new(_membership.Object, _users.Object);

    [Fact]
    public async Task NonMember_IsDeniedForEveryPermission()
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync((string?)null);
        var enforcer = Sut();

        foreach (var permission in Enum.GetValues<Permission>())
            (await enforcer.AuthorizeAsync(_userId, _conversationId, permission)).Should().BeFalse();
    }

    [Theory]
    [InlineData(Permission.Read)]
    [InlineData(Permission.Write)]
    [InlineData(Permission.Upload)]
    [InlineData(Permission.Download)]
    public async Task Member_HasReadWriteUploadDownload(Permission permission)
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Member");
        var enforcer = Sut();

        (await enforcer.AuthorizeAsync(_userId, _conversationId, permission)).Should().BeTrue();
    }

    [Theory]
    [InlineData(Permission.AddMember)]
    [InlineData(Permission.RemoveMember)]
    [InlineData(Permission.TransferOwnership)]
    [InlineData(Permission.Admin)]
    public async Task Member_DeniedAdminActions(Permission permission)
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Member");
        var enforcer = Sut();

        (await enforcer.AuthorizeAsync(_userId, _conversationId, permission)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Owner", Permission.AddMember)]
    [InlineData("Owner", Permission.RemoveMember)]
    [InlineData("Owner", Permission.Admin)]
    [InlineData("Admin", Permission.AddMember)]
    [InlineData("Admin", Permission.RemoveMember)]
    [InlineData("Admin", Permission.Admin)]
    public async Task OwnerOrAdmin_AllowedAdminActions(string role, Permission permission)
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync(role);
        var enforcer = Sut();

        (await enforcer.AuthorizeAsync(_userId, _conversationId, permission)).Should().BeTrue();
    }

    [Fact]
    public async Task TransferOwnership_OnlyOwner_NotAdmin()
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Admin");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.TransferOwnership))
            .Should().BeFalse();

        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Owner");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.TransferOwnership))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ManageRoles_OnlyOwner_NotAdminNotMember()
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Member");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.ManageRoles))
            .Should().BeFalse();

        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Admin");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.ManageRoles))
            .Should().BeFalse();

        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Owner");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.ManageRoles))
            .Should().BeTrue();
    }

    [Fact]
    public async Task DeleteConversation_OnlyOwner_NotAdminNotMember()
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Member");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.DeleteConversation))
            .Should().BeFalse();

        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Admin");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.DeleteConversation))
            .Should().BeFalse();

        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Owner");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.DeleteConversation))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    [InlineData("Moderator")]
    public async Task ModerateMessages_AllowedForOwnerAdminModerator(string role)
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync(role);
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.ModerateMessages))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ModerateMessages_DeniedForMember()
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Member");
        (await Sut().AuthorizeAsync(_userId, _conversationId, Permission.ModerateMessages))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(Permission.AddMember)]
    [InlineData(Permission.RemoveMember)]
    [InlineData(Permission.ManageRoles)]
    [InlineData(Permission.Admin)]
    [InlineData(Permission.DeleteConversation)]
    public async Task Moderator_DeniedAllNonModerationAdminActions(Permission permission)
    {
        // Il Moderator ha SOLO ModerateMessages in più rispetto a un Member: niente poteri admin.
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Moderator");
        (await Sut().AuthorizeAsync(_userId, _conversationId, permission)).Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizePlatform_GrantsOnlyPlatformAdmins()
    {
        _users.Setup(u => u.IsPlatformAdminAsync(_userId)).ReturnsAsync(true);
        (await Sut().AuthorizePlatformAsync(_userId)).Should().BeTrue();

        _users.Setup(u => u.IsPlatformAdminAsync(_userId)).ReturnsAsync(false);
        (await Sut().AuthorizePlatformAsync(_userId)).Should().BeFalse();
    }
}
