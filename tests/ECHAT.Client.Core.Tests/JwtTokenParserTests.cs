using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ECHAT.Client.Core.Services;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class JwtTokenParserTests
{
    private readonly JwtTokenParser _parser = new();

    private static string MakeJwt(
        DateTime? expires = null,
        string? subject = null,
        string? nameIdentifier = null,
        IEnumerable<Claim>? extra = null)
    {
        var claims = new List<Claim>();
        if (nameIdentifier is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        if (extra is not null) claims.AddRange(extra);

        var exp = expires ?? DateTime.UtcNow.AddHours(1);
        var token = new JwtSecurityToken(
            issuer: "test",
            audience: "test",
            claims: claims,
            notBefore: exp.AddHours(-2),
            expires: exp,
            signingCredentials: null);

        if (subject is not null)
            token.Payload["sub"] = subject;

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void ParseClaimsFromJwt_ValidToken_ReturnsAllClaims()
    {
        var jwt = MakeJwt(
            nameIdentifier: "user-123",
            extra: new[] { new Claim("role", "admin"), new Claim("custom", "value") });

        var claims = _parser.ParseClaimsFromJwt(jwt).ToList();

        claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "user-123");
        claims.Should().Contain(c => c.Type == "role" && c.Value == "admin");
        claims.Should().Contain(c => c.Type == "custom" && c.Value == "value");
    }

    [Fact]
    public void ParseClaimsFromJwt_ExpiredToken_ReturnsEmpty()
    {
        var jwt = MakeJwt(expires: DateTime.UtcNow.AddHours(-1), nameIdentifier: "user-123");

        var claims = _parser.ParseClaimsFromJwt(jwt);

        claims.Should().BeEmpty();
    }

    [Fact]
    public void ParseClaimsFromJwt_MalformedToken_ReturnsEmpty()
    {
        var claims = _parser.ParseClaimsFromJwt("not-a-jwt");

        claims.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractUserId_ReturnsNameIdentifier_WhenPresent()
    {
        var jwt = MakeJwt(nameIdentifier: "user-123", subject: "subject-id");

        _parser.TryExtractUserId(jwt).Should().Be("user-123");
    }

    [Fact]
    public void TryExtractUserId_FallsBackToSubject_WhenNoNameIdentifier()
    {
        var jwt = MakeJwt(subject: "subject-id");

        _parser.TryExtractUserId(jwt).Should().Be("subject-id");
    }

    [Fact]
    public void TryExtractUserId_ReturnsUnknown_WhenNeitherPresent()
    {
        var jwt = MakeJwt();

        _parser.TryExtractUserId(jwt).Should().Be("(unknown)");
    }

    [Fact]
    public void TryExtractUserId_ReturnsUnparseable_WhenMalformed()
    {
        _parser.TryExtractUserId("not-a-jwt").Should().Be("(unparseable)");
    }

    [Fact]
    public void IsTokenExpired_PastInstant_IsTrue()
    {
        _parser.IsTokenExpired(DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public void IsTokenExpired_FutureInstant_IsFalse()
    {
        _parser.IsTokenExpired(DateTime.UtcNow.AddHours(1)).Should().BeFalse();
    }
}
