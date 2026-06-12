using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class JwtTokenServiceTests
{
    private readonly JwtTokenService _service = new();

    private static UserRecord SampleUser() => new()
    {
        Id = Guid.NewGuid(),
        GoogleSubjectId = "google-sub-123",
        Email = "user@example.com",
        DisplayName = "Test User",
        PictureUrl = "https://example.com/pic.jpg",
        PlatformRole = "Admin"
    };

    private static JwtTokenOptions Options(int minutes = 60) => new()
    {
        Secret = "this_is_a_very_long_test_secret_key_at_least_32_chars",
        Issuer = "TEST_ISSUER",
        Audience = "TEST_AUDIENCE",
        ExpirationMinutes = minutes
    };

    [Fact]
    public void GenerateToken_ProducesParseableJwt()
    {
        var result = _service.GenerateToken(SampleUser(), Options());

        result.Token.Should().NotBeNullOrWhiteSpace();
        var handler = new JwtSecurityTokenHandler();
        handler.CanReadToken(result.Token).Should().BeTrue();
    }

    [Fact]
    public void GenerateToken_ContainsAllRequiredClaims()
    {
        var user = SampleUser();

        var token = new JwtSecurityTokenHandler().ReadJwtToken(_service.GenerateToken(user, Options()).Token);

        token.Claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == user.Id.ToString());
        token.Claims.Should().Contain(c => c.Type == ClaimTypes.Email && c.Value == user.Email);
        token.Claims.Should().Contain(c => c.Type == ClaimTypes.Name && c.Value == user.DisplayName);
        token.Claims.Should().Contain(c => c.Type == "picture" && c.Value == user.PictureUrl);
        token.Claims.Should().Contain(c => c.Type == "google_sub" && c.Value == user.GoogleSubjectId);
        token.Claims.Should().Contain(c => c.Type == "PlatformRole" && c.Value == user.PlatformRole);
    }

    [Fact]
    public void GenerateToken_HonorsIssuerAndAudience()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(_service.GenerateToken(SampleUser(), Options()).Token);

        token.Issuer.Should().Be("TEST_ISSUER");
        token.Audiences.Should().Contain("TEST_AUDIENCE");
    }

    [Fact]
    public void GenerateToken_NullPictureUrl_EmitsEmptyPictureClaim()
    {
        var user = new UserRecord
        {
            Id = Guid.NewGuid(),
            GoogleSubjectId = "sub",
            Email = "a@b.com",
            DisplayName = "A",
            PictureUrl = null,
            PlatformRole = "User"
        };

        var token = new JwtSecurityTokenHandler().ReadJwtToken(_service.GenerateToken(user, Options()).Token);

        token.Claims.Should().Contain(c => c.Type == "picture" && c.Value == "");
    }

    [Fact]
    public void DefaultOptions_HaveSafeIssuerAudienceDefaultsButNoSecret()
    {
        // Issuer/audience keep harmless defaults; the secret must NOT have a default value.
        var defaults = new JwtTokenOptions();

        defaults.Issuer.Should().Be("ECHAT");
        defaults.Audience.Should().Be("ECHAT");
        defaults.ExpirationMinutes.Should().Be(1440);
        defaults.Secret.Should().BeNull();
    }

    [Fact]
    public void GenerateToken_DefaultOptions_ThrowsBecauseSecretMissing()
    {
        // No secret configured => must fail closed, never sign with a known/default key.
        var act = () => _service.GenerateToken(SampleUser(), new JwtTokenOptions());

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("31_chars_secret_padding_padding")] // 31 chars, just under the 32 minimum
    public void GenerateToken_MissingEmptyOrTooShortSecret_Throws(string? secret)
    {
        var options = new JwtTokenOptions
        {
            Secret = secret,
            Issuer = "TEST_ISSUER",
            Audience = "TEST_AUDIENCE"
        };

        var act = () => _service.GenerateToken(SampleUser(), options);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GenerateToken_SecretAtMinimumLength_Succeeds()
    {
        var secret = new string('k', JwtTokenOptions.MinSecretLength);

        var options = new JwtTokenOptions { Secret = secret, Issuer = "ECHAT", Audience = "ECHAT" };

        var result = _service.GenerateToken(SampleUser(), options);

        result.Token.Should().NotBeNullOrWhiteSpace();
        new JwtSecurityTokenHandler().CanReadToken(result.Token).Should().BeTrue();
    }

    [Fact]
    public void GenerateToken_ExpirationMatchesConfiguredMinutes()
    {
        var before = DateTime.UtcNow;

        var result = _service.GenerateToken(SampleUser(), Options(minutes: 120));

        // ValidTo is truncated to whole seconds; allow a small tolerance window.
        result.ExpiresAt.Should().BeCloseTo(before.AddMinutes(120), TimeSpan.FromSeconds(5));
    }
}
