using System.Security.Claims;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserSyncService _userSync;
    private readonly IUserStore _users;
    private readonly IConfiguration _config;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<AuthController> _logger;

    public AuthController(UserSyncService userSync, IUserStore users, IConfiguration config, JwtTokenService jwt, ILogger<AuthController> logger)
    {
        _userSync = userSync;
        _users = users;
        _config = config;
        _jwt = jwt;
        _logger = logger;
    }

    /// <summary>
    /// Avvia il login OAuth con Google e redirige al consenso.
    /// Dopo l'autenticazione il middleware gestisce internamente /api/auth/google-callback,
    /// poi torna su /api/auth/google-signed-in dove emettiamo il JWT.
    /// </summary>
    [HttpGet("google-login")]
    public async Task<IActionResult> GoogleLogin(
        [FromServices] IAuthenticationSchemeProvider schemes,
        [FromQuery] string? returnUrl = "/")
    {
        // Lo schema Google viene registrato in Program.cs SOLO se ClientId/ClientSecret sono
        // configurati. Se manca, Challenge("Google") esploderebbe con un 500 non gestito che
        // abbatte l'intera richiesta. Controlliamo prima e rispondiamo in modo pulito.
        if (await schemes.GetSchemeAsync(GoogleDefaults.AuthenticationScheme) is null)
        {
            _logger.LogWarning(
                "Google OAuth login requested but the '{Scheme}' scheme is not registered " +
                "(Authentication:Google:ClientId/ClientSecret not configured).",
                GoogleDefaults.AuthenticationScheme);
            return Problem(
                title: "Google login unavailable",
                detail: "Google sign-in is not configured on this server.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        _logger.LogInformation("Google OAuth login challenge issued: returnUrl={ReturnUrl}", returnUrl);
        var properties = new AuthenticationProperties
        {
            RedirectUri = $"/api/auth/google-signed-in?returnUrl={Uri.EscapeDataString(returnUrl ?? "/login")}"
        };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Passo finale dopo OAuth: legge il cookie temporaneo, delega l'upsert a
    /// <see cref="UserSyncService"/>, emette il JWT, pulisce il cookie e redirige alla SPA.
    /// </summary>
    [HttpGet("google-signed-in")]
    public async Task<IActionResult> GoogleSignedIn([FromQuery] string? returnUrl = "/")
    {
        var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal == null)
        {
            _logger.LogWarning(
                "Google OAuth callback authentication failed: failure={Failure}",
                result.Failure?.Message ?? "(no principal)");
            return Unauthorized(new { error = "Google authentication failed." });
        }

        var claims = result.Principal.Claims.ToList();
        var googleSubjectId = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        var email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
        var name = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;
        var picture = claims.FirstOrDefault(c => c.Type == "picture")?.Value
                   ?? claims.FirstOrDefault(c => c.Type == "urn:google:picture")?.Value
                   ?? claims.FirstOrDefault(c => c.Type == "image")?.Value;

        if (string.IsNullOrEmpty(googleSubjectId) || string.IsNullOrEmpty(email))
        {
            _logger.LogWarning(
                "Google OAuth callback missing required claims: hasSub={HasSub} hasEmail={HasEmail}",
                !string.IsNullOrEmpty(googleSubjectId), !string.IsNullOrEmpty(email));
            return BadRequest(new { error = "Missing Google claims (sub or email)." });
        }

        var user = await _userSync.UpsertGoogleUserAsync(googleSubjectId, email, name ?? email, picture);

        var token = GenerateJwtToken(user);

        // Pulisce il cookie temporaneo di auth esterna
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        _logger.LogInformation(
            "Google OAuth login succeeded: userId={UserId} platformRole={PlatformRole}",
            user.Id, user.PlatformRole);

        // Redirige alla pagina di login della SPA con il token: Login.razor lo memorizza e va a /
        return Redirect($"/login?token={token}");
    }

    /// <summary>Restituisce il profilo dell'utente autenticato.</summary>
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var user = await _users.FindByIdAsync(userId);
        if (user == null) return NotFound();

        return Ok(new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            user.PictureUrl,
            user.CreatedAt,
            user.LastLoginAt,
            user.IsActive
        });
    }

    private string GenerateJwtToken(UserRecord user)
    {
        var jwtSection = _config.GetSection("Authentication:Jwt");
        var options = new JwtTokenOptions
        {
            // Nessun default per il segreto: se manca/è troppo corto JwtTokenService lancia.
            // Mai reintrodurre una chiave nota qui, anche se Program.cs la valida all'avvio.
            Secret = jwtSection["Secret"],
            Issuer = jwtSection["Issuer"] ?? "ECHAT",
            Audience = jwtSection["Audience"] ?? "ECHAT",
            ExpirationMinutes = int.TryParse(jwtSection["ExpirationMinutes"], out var mins) ? mins : 1440
        };

        var result = _jwt.GenerateToken(user, options);

        _logger.LogInformation(
            "JWT issued: userId={UserId} expiresAt={ExpiresAt}",
            user.Id, result.ExpiresAt);

        return result.Token;
    }
}
