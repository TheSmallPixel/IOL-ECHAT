using ECHAT.Server.App.Data;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

public class UserSyncServiceTests : IDisposable
{
    private readonly EchatDbContext _db;

    public UserSyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private UserSyncService Sut() => new(new UserStore(_db, new ECHAT.Server.Core.Services.UserUpsertService()));

    [Fact]
    public async Task UpsertGoogleUser_FirstTime_InsertsNewRow()
    {
        var user = await Sut().UpsertGoogleUserAsync("g-sub-1", "u@x", "Mario", "http://avatar");

        user.GoogleSubjectId.Should().Be("g-sub-1");
        user.Email.Should().Be("u@x");
        user.DisplayName.Should().Be("Mario");
        user.PictureUrl.Should().Be("http://avatar");
        (await _db.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertGoogleUser_Existing_UpdatesEmailNamePictureLastLogin()
    {
        var first = await Sut().UpsertGoogleUserAsync("g-sub-1", "old@x", "Vecchio", "http://old");
        var firstLogin = first.LastLoginAt;

        await Task.Delay(10);
        var second = await Sut().UpsertGoogleUserAsync("g-sub-1", "new@x", "Nuovo", "http://new");

        second.Id.Should().Be(first.Id, "stesso GoogleSubjectId, stessa riga");
        second.Email.Should().Be("new@x");
        second.DisplayName.Should().Be("Nuovo");
        second.PictureUrl.Should().Be("http://new");
        second.LastLoginAt.Should().BeAfter(firstLogin!.Value);
        (await _db.Users.CountAsync()).Should().Be(1);
    }
}
