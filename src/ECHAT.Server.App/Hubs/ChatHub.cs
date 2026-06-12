using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ECHAT.Server.App.Hubs;

/// <summary>
/// Hub di chat: gli eventi vengono indirizzati per utente (<c>Clients.Users(...)</c>) usando il
/// claim <c>NameIdentifier</c> del JWT, non per gruppo. Non servono più i metodi di join/leave:
/// il <see cref="ECHAT.Server.App.Services.SignalRNotifier"/> risolve l'audience ad ogni invio
/// interrogando lo store dei membri, quindi un utente rimosso smette subito di ricevere eventi.
/// </summary>
[Authorize]
public class ChatHub : Hub
{
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(ILogger<ChatHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "(unknown)";
        _logger.LogInformation(
            "ChatHub connected: connectionId={ConnectionId} userId={UserId}",
            Context.ConnectionId, userId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "(unknown)";
        if (exception != null)
        {
            _logger.LogWarning(exception,
                "ChatHub disconnected with error: connectionId={ConnectionId} userId={UserId}",
                Context.ConnectionId, userId);
        }
        else
        {
            _logger.LogInformation(
                "ChatHub disconnected: connectionId={ConnectionId} userId={UserId}",
                Context.ConnectionId, userId);
        }
        return base.OnDisconnectedAsync(exception);
    }
}
