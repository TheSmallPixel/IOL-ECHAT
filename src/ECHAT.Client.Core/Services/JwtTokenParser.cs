using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Implementazione di <see cref="ITokenParser"/> basata su <see cref="JwtSecurityTokenHandler"/>.
/// Logica pura: nessun I/O, nessun logging (l'App logga ai bordi se serve).
/// </summary>
public class JwtTokenParser : ITokenParser
{
    public IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(jwt);

            if (IsTokenExpired(token.ValidTo))
                return Enumerable.Empty<Claim>();

            return token.Claims;
        }
        catch
        {
            return Enumerable.Empty<Claim>();
        }
    }

    public string TryExtractUserId(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(jwt);
            return token.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                ?? token.Subject
                ?? "(unknown)";
        }
        catch
        {
            return "(unparseable)";
        }
    }

    public bool IsTokenExpired(DateTime validTo) => validTo < DateTime.UtcNow;
}
