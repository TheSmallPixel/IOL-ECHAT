using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Paginazione a cursore sopra il chat SDK con semantica cache-aside: legge prima da
/// <see cref="ILocalStore"/>, in fallback fa il fetch via <see cref="IChatSdk"/> e salva
/// i messaggi recuperati nello store locale.
/// </summary>
public class ScrollLoader : IScrollLoader
{
    private readonly IChatSdk _sdk;
    private readonly ILocalStore _store;

    public ScrollLoader(IChatSdk sdk, ILocalStore store)
    {
        _sdk = sdk;
        _store = store;
    }

    public async Task<List<DecryptedMessage>> LoadMoreAsync(Guid conversationId, long beforeSeq, int limit)
    {
        var cached = await _store.GetMessagesAsync(conversationId, afterSeq: null, beforeSeq: beforeSeq, limit);
        if (cached.Count >= limit) return cached;

        var fetched = await _sdk.FetchMessagesAsync(conversationId, afterSeq: null, beforeSeq: beforeSeq, limit);
        foreach (var m in fetched)
            await _store.StoreMessageAsync(conversationId, m);

        return fetched;
    }

    public async Task<List<DecryptedMessage>> LoadRecentAsync(Guid conversationId, long afterSeq, int limit)
    {
        var fetched = await _sdk.FetchMessagesAsync(conversationId, afterSeq: afterSeq, beforeSeq: null, limit);
        foreach (var m in fetched)
            await _store.StoreMessageAsync(conversationId, m);

        if (fetched.Count > 0)
        {
            var maxSeq = fetched.Max(m => m.Seq);
            await _store.SetLastAppliedSeqAsync(conversationId, maxSeq);
        }

        return fetched;
    }
}
