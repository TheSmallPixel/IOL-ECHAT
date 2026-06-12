using System.Net.Http.Json;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Adapter HTTP per <see cref="IChatServerGateway"/>. Tutta la conoscenza degli endpoint REST
/// e dell'header di Authorization vive qui; il flusso dei messaggi orchestrato in Client.Core
/// è indipendente da HttpClient.
/// </summary>
public class HttpChatServerGateway : IChatServerGateway
{
    private readonly HttpClient _http;
    private readonly TokenAuthStateProvider _authState;

    public HttpChatServerGateway(HttpClient http, TokenAuthStateProvider authState)
    {
        _http = http;
        _authState = authState;
    }

    private async Task EnsureAuthHeaderAsync()
    {
        var token = await _authState.GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<Guid> GetCurrentUserIdAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var idClaim = state.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idClaim, out var uid) ? uid : Guid.Empty;
    }

    public async Task<List<WrappedKey>> GetKeysAsync(Guid conversationId, int? epochId = null)
    {
        await EnsureAuthHeaderAsync();
        var url = $"api/conversations/{conversationId}/keys";
        if (epochId.HasValue) url += $"?epochId={epochId.Value}";
        var wraps = await _http.GetFromJsonAsync<List<WrappedKey>>(url);
        return wraps ?? new List<WrappedKey>();
    }

    public async Task<MessageEnvelope?> GetLatestEnvelopeAsync(Guid conversationId)
    {
        await EnsureAuthHeaderAsync();
        try
        {
            var response = await _http.GetAsync($"api/conversations/{conversationId}/messages/latest");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadFromJsonAsync<LatestMessageResponse>();
            if (json == null || !json.Exists || json.Envelope == null) return null;
            return json.Envelope;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SeqReservation> ReserveSeqAsync(Guid conversationId, int count)
    {
        await EnsureAuthHeaderAsync();
        var reservation = await _http.PostAsync(
            $"api/conversations/{conversationId}/seq/reserve?count={count}", null);
        reservation.EnsureSuccessStatusCode();
        return (await reservation.Content.ReadFromJsonAsync<SeqReservation>())!;
    }

    public async Task PostMessageAsync(MessageEnvelope envelope)
    {
        await EnsureAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync(
            $"api/conversations/{envelope.ConversationId}/messages", envelope);
        response.EnsureSuccessStatusCode();
    }

    public async Task ModerateMessageAsync(Guid conversationId, long seq, bool hidden, string? reason)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{seq}/moderate",
            new ModerateMessageRequest { Hidden = hidden, Reason = reason });
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<MessageEnvelope>> FetchEnvelopesAsync(
        Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        await EnsureAuthHeaderAsync();
        var url = $"api/conversations/{conversationId}/messages?limit={limit}";
        if (afterSeq.HasValue) url += $"&afterSeq={afterSeq}";
        if (beforeSeq.HasValue) url += $"&beforeSeq={beforeSeq}";
        return await _http.GetFromJsonAsync<List<MessageEnvelope>>(url) ?? new();
    }

    public async Task<int> CountEnvelopesBelowEpochAsync(Guid conversationId, int epochBelow)
    {
        await EnsureAuthHeaderAsync();
        var dto = await _http.GetFromJsonAsync<CountResponse>(
            $"api/conversations/{conversationId}/messages/count?epochBelow={epochBelow}");
        return dto?.Count ?? 0;
    }

    public async Task<List<ChainBoundary>> GetChainBoundariesAsync(Guid conversationId)
    {
        await EnsureAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<ChainBoundary>>(
            $"api/conversations/{conversationId}/messages/chain-boundaries") ?? new();
    }

    public async Task<List<DevicePublicKey>> GetConversationDevicesAsync(Guid conversationId)
    {
        await EnsureAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<DevicePublicKey>>(
            $"api/conversations/{conversationId}/devices") ?? new();
    }

    public async Task<DevicePublicKey?> GetConversationSenderDeviceAsync(Guid conversationId, Guid deviceId)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.GetAsync($"api/conversations/{conversationId}/devices/{deviceId}");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DevicePublicKey>();
    }

    public async Task PostKeysAsync(Guid conversationId, List<WrappedKey> wraps)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"api/conversations/{conversationId}/keys", wraps);
        resp.EnsureSuccessStatusCode();
    }

    public async Task RegisterDeviceAsync(DeviceRegistration registration)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("api/devices/register", registration);
        resp.EnsureSuccessStatusCode();
    }

    private sealed record CountResponse(int Count);

    public async Task AddMemberAsync(Guid conversationId, Guid userId, bool includeHistory)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/conversations/{conversationId}/members",
            new { userId, includeHistory });
        resp.EnsureSuccessStatusCode();
    }

    public async Task<int> RemoveMemberAsync(Guid conversationId, Guid userId)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.DeleteAsync($"api/conversations/{conversationId}/members/{userId}");
        resp.EnsureSuccessStatusCode();
        // Il server è autoritativo sul nuovo epoch (IncrementEpochAsync serializzato): lo usiamo per
        // provisionare la CEK del nuovo epoch, senza assumere oldEpoch+1 lato client (race-free).
        var body = await resp.Content.ReadFromJsonAsync<RemoveMemberResponse>();
        return body?.NewEpochId ?? 0;
    }

    private sealed record RemoveMemberResponse(string Message, int NewEpochId);

    public async Task SetMemberRoleAsync(Guid conversationId, Guid userId, string role)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/conversations/{conversationId}/members/{userId}/role",
            new { role });
        resp.EnsureSuccessStatusCode();
    }

    public async Task RenameConversationAsync(Guid conversationId, string newName)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync(
            $"api/conversations/{conversationId}",
            new { name = newName });
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteConversationAsync(Guid conversationId)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.DeleteAsync($"api/conversations/{conversationId}");
        resp.EnsureSuccessStatusCode();
    }

    public async Task<Guid> StartMigrationAsync(Guid conversationId, MigrationMode mode)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsync(
            $"api/conversations/{conversationId}/migration/start?mode={mode}", content: null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<StartMigrationResponse>();
        return body?.JobId ?? Guid.Empty;
    }

    public async Task CheckpointMigrationAsync(Guid conversationId, Guid jobId, int batchId, int progressPercent)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsync(
            $"api/conversations/{conversationId}/migration/{jobId}/checkpoint?batchId={batchId}&progress={progressPercent}",
            content: null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task FinalizeMigrationAsync(Guid conversationId, Guid jobId)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsync(
            $"api/conversations/{conversationId}/migration/{jobId}/finalize",
            content: null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CancelMigrationAsync(Guid conversationId, Guid jobId)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsync(
            $"api/conversations/{conversationId}/migration/{jobId}/cancel",
            content: null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ForceFinalizeMigrationAsync(Guid conversationId, Guid jobId)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsync(
            $"api/conversations/{conversationId}/migration/{jobId}/force-finalize",
            content: null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ReplaceMessageAsync(Guid conversationId, long seq, MessageEnvelope envelope)
    {
        await EnsureAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{seq}/replace", envelope);
        resp.EnsureSuccessStatusCode();
    }

    public async Task PostTombstonesAsync(Guid conversationId, IEnumerable<TombstoneRecord> tombstones)
    {
        await EnsureAuthHeaderAsync();
        var payload = tombstones.Select(t => new
        {
            messageId = t.MessageId,
            seq = t.Seq,
            epochId = t.EpochId,
            nonce = Array.Empty<byte>(),
            ciphertext = Array.Empty<byte>(),
            signature = Array.Empty<byte>()
        }).ToList();

        var resp = await _http.PostAsJsonAsync(
            $"api/conversations/{conversationId}/tombstones",
            new { tombstones = payload });
        resp.EnsureSuccessStatusCode();
    }

    private class LatestMessageResponse
    {
        public bool Exists { get; set; }
        public MessageEnvelope? Envelope { get; set; }
    }

    private class StartMigrationResponse
    {
        public Guid JobId { get; set; }
    }
}
