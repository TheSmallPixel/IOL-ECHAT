using System.Security.Claims;
using ECHAT.Client.Core.Interfaces;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace ECHAT.Client.App.Services;

public class TokenAuthStateProvider : AuthenticationStateProvider
{
    private readonly IJSRuntime _js;
    private readonly ITokenParser _tokenParser;
    private readonly ILogger<TokenAuthStateProvider> _logger;
    private const string TokenKey = "echat_token";

    public TokenAuthStateProvider(IJSRuntime js, ITokenParser tokenParser, ILogger<TokenAuthStateProvider> logger)
    {
        _js = js;
        _tokenParser = tokenParser;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await GetTokenAsync();
        var identity = string.IsNullOrEmpty(token)
            ? new ClaimsIdentity()
            : new ClaimsIdentity(_tokenParser.ParseClaimsFromJwt(token), "jwt");

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task<string?> GetTokenAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read auth token from localStorage");
            return null;
        }
    }

    public async Task LoginAsync(string token)
    {
        await _js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
        var userId = _tokenParser.TryExtractUserId(token);
        _logger.LogInformation("User login completed: userId={UserId}", userId);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task LogoutAsync()
    {
        var existing = await GetTokenAsync();
        var userId = string.IsNullOrEmpty(existing) ? "(unknown)" : _tokenParser.TryExtractUserId(existing);
        await _js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        _logger.LogInformation("User logout completed: userId={UserId}", userId);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
