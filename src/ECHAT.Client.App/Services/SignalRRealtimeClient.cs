using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Enums;
using ECHAT.Models.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ECHAT.Client.App.Services;

public class SignalRRealtimeClient : IRealtimeClient, IAsyncDisposable
{
    private HubConnection? _hub;
    private readonly NavigationManager _nav;
    private readonly TokenAuthStateProvider _authState;
    private readonly ILogger<SignalRRealtimeClient> _logger;

    public event Action<MessageAvailableEvent>? OnMessageAvailable;
    public event Action<EpochRotatedEvent>? OnEpochRotated;
    public event Action<MemberChangedEvent>? OnMemberChanged;
    public event Action<MessageModeratedEvent>? OnMessageModerated;
    public event Action<ConversationChangedEvent>? OnConversationChanged;
    public event Action<JobProgressEvent>? OnJobProgress;
    public event Action<ConnectionState>? OnConnectionStateChanged;

    public SignalRRealtimeClient(NavigationManager nav, TokenAuthStateProvider authState, ILogger<SignalRRealtimeClient> logger)
    {
        _nav = nav;
        _authState = authState;
        _logger = logger;
    }

    public async Task ConnectAsync()
    {
        if (_hub != null) return;

        var token = await _authState.GetTokenAsync();
        var baseUri = _nav.BaseUri.TrimEnd('/');

        _hub = new HubConnectionBuilder()
            .WithUrl($"{baseUri}/hubs/chat", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<MessageAvailableEvent>("MessageAvailable", evt =>
        {
            try { OnMessageAvailable?.Invoke(evt); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "MessageAvailable handler threw for conversation {ConversationId} seq {Seq}",
                    evt.ConversationId, evt.Seq);
            }
        });

        _hub.On<EpochRotatedEvent>("EpochRotated", evt =>
        {
            try { OnEpochRotated?.Invoke(evt); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EpochRotated handler threw for conversation {ConversationId} newEpoch {NewEpochId}",
                    evt.ConversationId, evt.NewEpochId);
            }
        });

        _hub.On<MessageModeratedEvent>("MessageModerated", evt =>
        {
            try { OnMessageModerated?.Invoke(evt); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "MessageModerated handler threw for conversation {ConversationId} seq {Seq} hidden {Hidden}",
                    evt.ConversationId, evt.Seq, evt.Hidden);
            }
        });

        _hub.On<MemberChangedEvent>("MemberChanged", evt =>
        {
            try { OnMemberChanged?.Invoke(evt); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "MemberChanged handler threw for conversation {ConversationId} user {UserId} action {Action}",
                    evt.ConversationId, evt.UserId, evt.Action);
            }
        });

        _hub.On<ConversationChangedEvent>("ConversationChanged", evt =>
        {
            try { OnConversationChanged?.Invoke(evt); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ConversationChanged handler threw for conversation {ConversationId} change {ChangeType}",
                    evt.ConversationId, evt.ChangeType);
            }
        });

        _hub.On<JobProgressEvent>("JobProgress", evt =>
        {
            try { OnJobProgress?.Invoke(evt); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "JobProgress handler threw for job {JobId}",
                    evt.JobId);
            }
        });

        _hub.Reconnecting += ex =>
        {
            if (ex != null)
                _logger.LogWarning(ex, "SignalR reconnecting after error");
            else
                _logger.LogInformation("SignalR reconnecting");
            OnConnectionStateChanged?.Invoke(ConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _hub.Reconnected += connectionId =>
        {
            _logger.LogInformation("SignalR reconnected: connectionId={ConnectionId}", connectionId);
            OnConnectionStateChanged?.Invoke(ConnectionState.Connected);
            return Task.CompletedTask;
        };
        _hub.Closed += ex =>
        {
            if (ex != null)
                _logger.LogWarning(ex, "SignalR connection closed with error");
            else
                _logger.LogInformation("SignalR connection closed");
            OnConnectionStateChanged?.Invoke(ConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        _logger.LogInformation("SignalR connecting to {HubUrl}", $"{baseUri}/hubs/chat");
        OnConnectionStateChanged?.Invoke(ConnectionState.Connecting);
        await _hub.StartAsync();
        _logger.LogInformation("SignalR connected: connectionId={ConnectionId}", _hub.ConnectionId);
        OnConnectionStateChanged?.Invoke(ConnectionState.Connected);
    }

    public async Task DisconnectAsync()
    {
        if (_hub != null)
        {
            _logger.LogInformation("SignalR disconnecting");
            await _hub.StopAsync();
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
