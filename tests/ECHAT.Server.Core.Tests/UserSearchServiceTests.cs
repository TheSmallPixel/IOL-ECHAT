using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests;

public class UserSearchServiceTests
{
    private readonly Mock<IUserStore> _store = new();
    private readonly UserSearchService _sut;
    private readonly Guid _me = Guid.NewGuid();

    public UserSearchServiceTests()
    {
        _sut = new UserSearchService(_store.Object);
    }

    private void SetupCandidates(params UserSearchCandidate[] candidates)
    {
        _store.Setup(s => s.SearchUsersAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(candidates.ToList());
    }

    private static UserSearchCandidate User(string email, string display, bool active = true, Guid? id = null)
        => new(id ?? Guid.NewGuid(), email, display, "pic", active);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_NullOrWhitespace_ReturnsEmptyWithoutHittingStore(string? query)
    {
        var result = await _sut.SearchAsync(_me, query!);

        result.Should().BeEmpty();
        _store.Verify(s => s.SearchUsersAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_QueryTooShort_ReturnsEmpty()
    {
        var result = await _sut.SearchAsync(_me, "a");

        result.Should().BeEmpty();
        _store.Verify(s => s.SearchUsersAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_FiltersInactiveUsers()
    {
        SetupCandidates(
            User("alice@x.com", "alice", active: true),
            User("alicia@x.com", "alicia", active: false));

        var result = await _sut.SearchAsync(_me, "ali");

        result.Should().ContainSingle(u => u.Email == "alice@x.com");
    }

    [Fact]
    public async Task SearchAsync_ExcludesCurrentUser()
    {
        SetupCandidates(
            User("me@x.com", "metoo", id: _me),
            User("other@x.com", "meother"));

        var result = await _sut.SearchAsync(_me, "me");

        result.Should().ContainSingle(u => u.Email == "other@x.com");
    }

    [Fact]
    public async Task SearchAsync_MatchesEmailSubstring()
    {
        SetupCandidates(
            User("bob@example.com", "bob"),
            User("carol@other.com", "carol"));

        var result = await _sut.SearchAsync(_me, "example");

        result.Should().ContainSingle(u => u.DisplayName == "bob");
    }

    [Fact]
    public async Task SearchAsync_MatchesDisplayNameSubstring()
    {
        SetupCandidates(
            User("x@x.com", "Jonathan"),
            User("y@y.com", "Mary"));

        var result = await _sut.SearchAsync(_me, "nath");

        result.Should().ContainSingle(u => u.DisplayName == "Jonathan");
    }

    [Fact]
    public async Task SearchAsync_LimitsToTwenty()
    {
        var candidates = Enumerable.Range(0, 30)
            .Select(i => User($"user{i}@match.com", $"user{i}"))
            .ToArray();
        SetupCandidates(candidates);

        var result = await _sut.SearchAsync(_me, "match");

        result.Should().HaveCount(20);
    }

    [Fact]
    public async Task SearchAsync_ProjectsCorrectDtoFields()
    {
        var id = Guid.NewGuid();
        _store.Setup(s => s.SearchUsersAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UserSearchCandidate>
            {
                new(id, "dto@x.com", "Dto Name", "https://pic", true)
            });

        var result = await _sut.SearchAsync(_me, "dto");

        var dto = result.Should().ContainSingle().Subject;
        dto.Id.Should().Be(id);
        dto.Email.Should().Be("dto@x.com");
        dto.DisplayName.Should().Be("Dto Name");
        dto.PictureUrl.Should().Be("https://pic");
    }
}
