using System.Security.Claims;
using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ECHAT.Server.App.Authorization;

/// <summary>
/// Gate dichiarativo per le azioni conversation-scoped: legge {conversationId} dalla route e
/// l'id utente dal claim, poi delega la decisione a <see cref="IPolicyEnforcer"/> (Core, testabile).
/// Se il permesso manca  403 prima che il corpo dell'action venga eseguito.
///
/// La logica di autorizzazione (ruolo  permesso) vive interamente in Core; questo attributo è
/// solo la colla MVC che la invoca al confine HTTP. I service mantengono comunque i propri
/// controlli (defense-in-depth + regole di business non-ruolo, es. "l'Owner non si rimuove").
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireConversationPermissionAttribute : Attribute, IFilterFactory
{
    private readonly Permission _permission;
    private readonly string _routeKey;

    public RequireConversationPermissionAttribute(Permission permission, string routeKey = "conversationId")
    {
        _permission = permission;
        _routeKey = routeKey;
    }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new Filter(_permission, _routeKey, serviceProvider.GetRequiredService<IPolicyEnforcer>());

    private sealed class Filter : IAsyncAuthorizationFilter
    {
        private readonly Permission _permission;
        private readonly string _routeKey;
        private readonly IPolicyEnforcer _policy;

        public Filter(Permission permission, string routeKey, IPolicyEnforcer policy)
        {
            _permission = permission;
            _routeKey = routeKey;
            _policy = policy;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var sub = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(sub, out var userId))
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            if (!context.RouteData.Values.TryGetValue(_routeKey, out var raw)
                || !Guid.TryParse(raw?.ToString(), out var conversationId))
            {
                // Attributo applicato a una route senza {conversationId}: configurazione errata.
                context.Result = new ForbidResult();
                return;
            }

            if (!await _policy.AuthorizeAsync(userId, conversationId, _permission))
                context.Result = new ForbidResult();
        }
    }
}
