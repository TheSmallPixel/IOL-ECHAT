using System.Security.Claims;
using ECHAT.Server.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ECHAT.Server.App.Authorization;

/// <summary>
/// Gate dichiarativo per gli endpoint di piattaforma (/api/admin): delega a
/// <see cref="IPolicyEnforcer.AuthorizePlatformAsync"/> (Core, testabile). Non-admin  403 prima
/// che il corpo dell'action venga eseguito. Richiede comunque <c>[Authorize]</c> a monte per
/// l'autenticazione (anonimo  401, utente non-admin  403).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequirePlatformAdminAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new Filter(serviceProvider.GetRequiredService<IPolicyEnforcer>());

    private sealed class Filter : IAsyncAuthorizationFilter
    {
        private readonly IPolicyEnforcer _policy;

        public Filter(IPolicyEnforcer policy) => _policy = policy;

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var sub = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(sub, out var userId))
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            if (!await _policy.AuthorizePlatformAsync(userId))
                context.Result = new ForbidResult();
        }
    }
}
