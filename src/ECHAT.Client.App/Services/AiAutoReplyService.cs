using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using ECHAT.Models.Events;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Servizio di risposta automatica AI. Quando attivo ascolta i nuovi messaggi via SignalR
/// e delega a <see cref="IAiAutoReplyOrchestrator"/> (Client.Core) il flusso di reply
/// (ritardo, fetch contesto, skip self-reply, validazione). Qui resta solo l'event wiring,
/// lo stato di abilitazione, il cancel-on-new-message e il logging.
/// </summary>
public class AiAutoReplyService : IDisposable
{
    private readonly ChatSdkService _chatSdk;
    private readonly SignalRRealtimeClient _realtime;
    private readonly IAiAutoReplyOrchestrator _orchestrator;
    private readonly TokenAuthStateProvider _authState;
    private readonly ILogger<AiAutoReplyService> _logger;

    private Guid _activeConversationId;
    private bool _enabled;
    private CancellationTokenSource? _delayCts;
    private Guid _myUserId;
    private Dictionary<Guid, string> _memberNames = new();

    public bool IsEnabled => _enabled;
    public event Action? OnStateChanged;

    public AiAutoReplyService(
        ChatSdkService chatSdk,
        SignalRRealtimeClient realtime,
        IAiAutoReplyOrchestrator orchestrator,
        TokenAuthStateProvider authState,
        ILogger<AiAutoReplyService> logger)
    {
        _chatSdk = chatSdk;
        _realtime = realtime;
        _orchestrator = orchestrator;
        _authState = authState;
        _logger = logger;
    }

    public async Task ToggleAsync(Guid conversationId)
    {
        if (_enabled)
        {
            Disable();
            return;
        }

        _activeConversationId = conversationId;

        var state = await _authState.GetAuthenticationStateAsync();
        var idClaim = state.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(idClaim, out var uid)) _myUserId = uid;

        _enabled = true;
        _realtime.OnMessageAvailable += OnNewMessage;
        _logger.LogInformation(
            "AI auto-reply enabled for conversation {ConversationId} user {UserId}",
            _activeConversationId, _myUserId);
        OnStateChanged?.Invoke();
    }

    public void Disable()
    {
        var wasEnabled = _enabled;
        _enabled = false;
        _delayCts?.Cancel();
        _realtime.OnMessageAvailable -= OnNewMessage;
        if (wasEnabled)
        {
            _logger.LogInformation(
                "AI auto-reply disabled for conversation {ConversationId}",
                _activeConversationId);
        }
        OnStateChanged?.Invoke();
    }

    public void SetMemberNames(Dictionary<Guid, string> names)
    {
        _memberNames = names;
    }

    private async void OnNewMessage(MessageAvailableEvent evt)
    {
        if (!_enabled || evt.ConversationId != _activeConversationId) return;

        // Annulla la risposta precedente in attesa (reset timer al nuovo messaggio)
        _delayCts?.Cancel();
        _delayCts = new CancellationTokenSource();
        var ct = _delayCts.Token;

        try
        {
            // Tutto il flusso (ritardo, fetch contesto, skip self-reply, generazione, cleanup,
            // validazione) vive nell'orchestrator di Client.Core. Qui passiamo solo i delegati di I/O.
            await _orchestrator.RunAsync(
                _myUserId,
                _memberNames,
                fetchContext: _ => _chatSdk.FetchMessagesAsync(_activeConversationId, null, null, 20),
                sendReply: async (cleaned, _) =>
                {
                    await _chatSdk.SendMessageAsync(_activeConversationId, cleaned);
                    _logger.LogInformation(
                        "AI auto-reply sent for conversation {ConversationId} triggered by seq {Seq}",
                        _activeConversationId, evt.Seq);
                },
                ct);
        }
        catch (TaskCanceledException) { /* nuovo messaggio, timer resettato */ }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AI auto-reply failed for conversation {ConversationId}",
                _activeConversationId);
        }
    }

    public void Dispose()
    {
        Disable();
        _delayCts?.Dispose();
    }
}
