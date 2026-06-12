using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ECHAT.Server.Core.Interfaces;
using Microsoft.IdentityModel.Tokens;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Opzioni di firma del JWT. Vengono lette dalla configurazione nella App e passate qui,
/// così Core non dipende da <c>IConfiguration</c>. Il segreto NON ha default: deve essere
/// fornito da configurazione, altrimenti la generazione del token fallisce con eccezione.
/// Issuer/audience hanno default innocui.
/// </summary>
public class JwtTokenOptions
{
    /// <summary>
    /// Numero minimo di caratteri richiesti per la chiave di firma HS256.
    /// </summary>
    public const int MinSecretLength = 32;

    public string? Secret { get; init; }
    public string Issuer { get; init; } = "ECHAT";
    public string Audience { get; init; } = "ECHAT";
    public int ExpirationMinutes { get; init; } = 1440;
}

/// <summary>
/// Esito della generazione del token: la stringa serializzata e l'istante di scadenza
/// (utile per il logging lato App, che prima leggeva <c>token.ValidTo</c>).
/// </summary>
public class JwtTokenResult
{
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
}

/// <summary>
/// Genera il JWT per un utente: chiave simmetrica + credenziali di firma HS256, array di claim,
/// creazione e serializzazione di <see cref="JwtSecurityToken"/>. Il controller mantiene il flusso
/// OAuth/cookie/redirect e l'estrazione delle claim da Google.
/// </summary>
public class JwtTokenService
{
    public JwtTokenResult GenerateToken(UserRecord user, JwtTokenOptions options)
    {
        // Mai firmare con una chiave assente, vuota o troppo corta: una chiave nota/debole
        // permetterebbe a chiunque di forgiare token (incluso PlatformAdmin). Fail-closed.
        if (string.IsNullOrWhiteSpace(options.Secret) || options.Secret.Length < JwtTokenOptions.MinSecretLength)
        {
            throw new InvalidOperationException(
                $"JWT signing secret is missing or too short (must be at least {JwtTokenOptions.MinSecretLength} characters). " +
                "Configure 'Authentication:Jwt:Secret' with a long random value.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var tokenClaims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim("picture", user.PictureUrl ?? ""),
            new Claim("google_sub", user.GoogleSubjectId),
            new Claim("PlatformRole", user.PlatformRole)
        };

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: tokenClaims,
            expires: DateTime.UtcNow.AddMinutes(options.ExpirationMinutes),
            signingCredentials: creds
        );

        return new JwtTokenResult
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt = token.ValidTo
        };
    }
}
