using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.App.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

public class MembershipReaderTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly MembershipReader _sut;

    public MembershipReaderTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _sut = new MembershipReader(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task NonMember_ReturnsNull()
    {
        var role = await _sut.GetRoleAsync(Guid.NewGuid(), Guid.NewGuid());
        role.Should().BeNull();
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    [InlineData("Member")]
    public async Task ActiveMember_ReturnsRole(string role)
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        _db.Members.Add(new MemberEntity
        {
            ConversationId = conversationId,
            UserId = userId,
            Role = role,
            JoinedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        (await _sut.GetRoleAsync(userId, conversationId)).Should().Be(role);
    }

    [Fact]
    public async Task RemovedMember_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        _db.Members.Add(new MemberEntity
        {
            ConversationId = conversationId,
            UserId = userId,
            Role = "Member",
            JoinedAt = DateTime.UtcNow.AddDays(-1),
            RemovedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        (await _sut.GetRoleAsync(userId, conversationId)).Should().BeNull();
    }
}
