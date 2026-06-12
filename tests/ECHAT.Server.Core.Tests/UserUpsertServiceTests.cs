using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class UserUpsertServiceTests
{
    private readonly UserUpsertService _sut = new();
    private static readonly DateTime Now = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    private static GoogleUserUpsert Upsert(string? display = "New Name", string? picture = "https://new/pic") => new()
    {
        GoogleSubjectId = "sub-1",
        Email = "new@example.com",
        DisplayName = display ?? string.Empty,
        PictureUrl = picture
    };

    [Fact]
    public void BuildOrUpdate_NoExisting_CreatesNewUser()
    {
        var result = _sut.BuildOrUpdate(Upsert(), existing: null, Now);

        result.IsNew.Should().BeTrue();
        result.Email.Should().Be("new@example.com");
        result.DisplayName.Should().Be("New Name");
        result.PictureUrl.Should().Be("https://new/pic");
        result.CreatedAt.Should().Be(Now);
        result.LastLoginAt.Should().Be(Now);
    }

    [Fact]
    public void BuildOrUpdate_Existing_AlwaysUpdatesEmail()
    {
        var existing = new ExistingUserSnapshot { DisplayName = "Old", PictureUrl = "old", CreatedAt = Now.AddDays(-10) };

        var result = _sut.BuildOrUpdate(Upsert(), existing, Now);

        result.IsNew.Should().BeFalse();
        result.Email.Should().Be("new@example.com");
    }

    [Fact]
    public void BuildOrUpdate_Existing_UpdatesDisplayNameAndPictureWhenProvided()
    {
        var existing = new ExistingUserSnapshot { DisplayName = "Old", PictureUrl = "oldpic", CreatedAt = Now.AddDays(-10) };

        var result = _sut.BuildOrUpdate(Upsert(display: "New Name", picture: "newpic"), existing, Now);

        result.DisplayName.Should().Be("New Name");
        result.PictureUrl.Should().Be("newpic");
    }

    [Fact]
    public void BuildOrUpdate_Existing_KeepsPictureWhenUpsertPictureNull()
    {
        var existing = new ExistingUserSnapshot { DisplayName = "Old", PictureUrl = "keepme", CreatedAt = Now.AddDays(-10) };

        var result = _sut.BuildOrUpdate(Upsert(picture: null), existing, Now);

        result.PictureUrl.Should().Be("keepme");
    }

    [Fact]
    public void BuildOrUpdate_Existing_AlwaysUpdatesLastLoginAt()
    {
        var existing = new ExistingUserSnapshot { DisplayName = "Old", PictureUrl = "old", CreatedAt = Now.AddDays(-10) };

        var result = _sut.BuildOrUpdate(Upsert(), existing, Now);

        result.LastLoginAt.Should().Be(Now);
    }

    [Fact]
    public void BuildOrUpdate_Existing_PreservesCreatedAt()
    {
        var created = Now.AddDays(-365);
        var existing = new ExistingUserSnapshot { DisplayName = "Old", PictureUrl = "old", CreatedAt = created };

        var result = _sut.BuildOrUpdate(Upsert(), existing, Now);

        result.CreatedAt.Should().Be(created);
    }
}
