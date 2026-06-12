using ECHAT.Models.Events;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Interfaces;

public interface IRealtimeClient
{
    event Action<MessageAvailableEvent>? OnMessageAvailable;
    event Action<EpochRotatedEvent>? OnEpochRotated;
    event Action<MemberChangedEvent>? OnMemberChanged;
    event Action<MessageModeratedEvent>? OnMessageModerated;
    event Action<ConversationChangedEvent>? OnConversationChanged;
    event Action<JobProgressEvent>? OnJobProgress;
    event Action<ConnectionState>? OnConnectionStateChanged;
    Task ConnectAsync();
    Task DisconnectAsync();
}
