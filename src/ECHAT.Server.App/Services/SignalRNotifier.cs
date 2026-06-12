using ECHAT.Models.Events;
using ECHAT.Server.App.Hubs;
using ECHAT.Server.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace ECHAT.Server.App.Services;

public class SignalRNotifier : IRealtimeNotifier
{
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly IMemberStore _members;

    public SignalRNotifier(IHubContext<ChatHub> hubContext, IMemberStore members)
    {
        _hubContext = hubContext;
        _members = members;
    }

    public async Task NotifyAsync(Guid conversationId, RealtimeEvent evt)
    {
        // Risolviamo i destinatari ad ogni invio: così un utente rimosso (anche un secondo fa)
        // non riceve l'evento anche se la sua connessione SignalR non è ancora caduta.
        var active = await _members.ListActiveWithUserAsync(conversationId);
        if (active.Count == 0) return;
        var userIds = active.Select(m => m.UserId.ToString()).ToList();
        await SendAsync(userIds, evt);
    }

    public Task NotifyUsersAsync(IEnumerable<Guid> userIds, RealtimeEvent evt)
    {
        var ids = userIds.Select(u => u.ToString()).Distinct().ToList();
        if (ids.Count == 0) return Task.CompletedTask;
        return SendAsync(ids, evt);
    }

    private async Task SendAsync(IReadOnlyList<string> userIds, RealtimeEvent evt)
    {
        var (method, payload) = evt switch
        {
            MessageAvailableEvent m => ("MessageAvailable", (object)m),
            EpochRotatedEvent e    => ("EpochRotated",    (object)e),
            MemberChangedEvent mc  => ("MemberChanged",   (object)mc),
            MessageModeratedEvent mm => ("MessageModerated", (object)mm),
            ConversationChangedEvent c => ("ConversationChanged", (object)c),
            JobProgressEvent j     => ("JobProgress",     (object)j),
            _                       => ("Event",           (object)evt),
        };
        await _hubContext.Clients.Users(userIds).SendAsync(method, payload);
    }
}
