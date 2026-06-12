using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using MigrationProgress = ECHAT.Client.Core.Interfaces.MigrationProgress;

namespace ECHAT.Client.Core.Tests.Fakes;

public class FakeChatSdk : IChatSdk
{
    public Dictionary<Guid, List<DecryptedMessage>> ServerMessages { get; } = new();
    public List<(Guid conversationId, long? afterSeq, long? beforeSeq, int limit)> FetchCalls { get; } = new();

    public Task SendMessageAsync(Guid conversationId, string text, List<AttachmentRef>? attachments = null)
        => Task.CompletedTask;

    public Task ModerateMessageAsync(Guid conversationId, long seq, bool hidden, string? reason)
        => Task.CompletedTask;

    public Task<List<DecryptedMessage>> FetchMessagesAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        FetchCalls.Add((conversationId, afterSeq, beforeSeq, limit));
        if (!ServerMessages.TryGetValue(conversationId, out var list))
            return Task.FromResult(new List<DecryptedMessage>());

        IEnumerable<DecryptedMessage> q = list.OrderBy(m => m.Seq);
        if (afterSeq.HasValue) q = q.Where(m => m.Seq > afterSeq.Value);
        if (beforeSeq.HasValue) q = q.Where(m => m.Seq < beforeSeq.Value);
        return Task.FromResult(q.Take(limit).ToList());
    }

    public Task<Guid> UploadFileAsync(Guid conversationId, Stream fileStream, string fileName, string mimeType)
        => Task.FromResult(Guid.NewGuid());

    public Task<Stream> DownloadFileAsync(Guid conversationId, Guid fileId)
        => Task.FromResult<Stream>(new MemoryStream());

    public Task AddMemberAsync(Guid conversationId, Guid userId, bool includeHistory) => Task.CompletedTask;
    public Task ProvisionConversationKeysAsync(Guid conversationId, int epochId) => Task.CompletedTask;
    public Task GrantConversationKeysAsync(Guid conversationId) => Task.CompletedTask;
    public Task SetMemberRoleAsync(Guid conversationId, Guid userId, string role) => Task.CompletedTask;
    public Task RenameConversationAsync(Guid conversationId, string newName) => Task.CompletedTask;
    public Task DeleteConversationAsync(Guid conversationId) => Task.CompletedTask;
    public Task PreReserveSeqsAsync(Guid conversationId, int count) => Task.CompletedTask;
    public Task ForceFinalizeMigrationAsync(Guid conversationId, Guid jobId) => Task.CompletedTask;
    public Task RemoveMemberAsync(
        Guid conversationId,
        Guid userId,
        MigrationMode? migration,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
