using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// In-memory fake del gateway HTTP. Espone gli inviti effettuati e le chiavi note così i test
/// possono verificare cosa il flusso ha mandato al server.
/// </summary>
public class FakeChatServerGateway : IChatServerGateway
{
    // Lock per le mutazioni delle liste sotto: CustodianWorker chiama ReplaceMessageAsync da più
    // task concorrenti (Parallel.ForEachAsync col pool di Web Worker), e List<T> non è thread-safe.
    private readonly object _lock = new();

    public Guid CurrentUserId { get; set; } = Guid.NewGuid();
    public Dictionary<(Guid conversationId, int epochId), byte[]> Keys { get; } = new();
    public Dictionary<Guid, MessageEnvelope?> Latest { get; } = new();
    public Dictionary<Guid, List<MessageEnvelope>> Envelopes { get; } = new();
    public List<MessageEnvelope> Sent { get; } = new();
    public List<(Guid conversationId, long seq, MessageEnvelope envelope)> Replaced { get; } = new();
    public List<(Guid conversationId, Guid userId, bool includeHistory)> Adds { get; } = new();
    public List<(Guid conversationId, Guid userId)> Removes { get; } = new();
    public List<(Guid conversationId, MigrationMode mode, Guid jobId)> Migrations { get; } = new();
    public List<(Guid conversationId, Guid jobId, int batchId, int progress)> Checkpoints { get; } = new();
    public List<(Guid conversationId, Guid jobId)> Finalizes { get; } = new();
    public List<(Guid conversationId, List<TombstoneRecord> tombstones)> Tombstones { get; } = new();

    public int NextSeqStart { get; set; } = 1;
    public int LastSeqReservationCount { get; set; }
    /// <summary>Quante volte GetKeysAsync è stato chiamato, per provare che una cache-hit non interroga il server.</summary>
    public int GetKeysCallCount { get; set; }

    public Task<Guid> GetCurrentUserIdAsync() => Task.FromResult(CurrentUserId);

    public Task<List<WrappedKey>> GetKeysAsync(Guid conversationId, int? epochId = null)
    {
        GetKeysCallCount++;
        var wraps = Keys
            .Where(kv => kv.Key.conversationId == conversationId && (!epochId.HasValue || kv.Key.epochId == epochId.Value))
            .Select(kv => new WrappedKey
            {
                ConversationId = kv.Key.conversationId,
                EpochId = kv.Key.epochId,
                DeviceId = CurrentUserId,
                WrappedCek = kv.Value
            }).ToList();
        return Task.FromResult(wraps);
    }

    public Task<MessageEnvelope?> GetLatestEnvelopeAsync(Guid conversationId)
        => Task.FromResult(Latest.TryGetValue(conversationId, out var env) ? env : null);

    public Task<SeqReservation> ReserveSeqAsync(Guid conversationId, int count)
    {
        LastSeqReservationCount = count;
        var start = NextSeqStart;
        var end = start + count - 1;
        NextSeqStart = end + 1;
        return Task.FromResult(new SeqReservation
        {
            StartSeq = start,
            EndSeq = end,
            LeaseToken = $"lease-{Guid.NewGuid():N}",
            AnchorSeq = 0,
            AnchorEnvelopeHash = Array.Empty<byte>()
        });
    }

    public Task PostMessageAsync(MessageEnvelope envelope)
    {
        // CustodianWorker e altri callers eseguono POST in parallelo via Parallel.ForEachAsync;
        // List<T> non è thread-safe quindi serializziamo le mutazioni.
        lock (_lock)
        {
            Sent.Add(envelope);
            if (!Envelopes.TryGetValue(envelope.ConversationId, out var list))
                Envelopes[envelope.ConversationId] = list = new();
            list.Add(envelope);
            Latest[envelope.ConversationId] = envelope;
        }
        return Task.CompletedTask;
    }

    public List<(Guid conversationId, long seq, bool hidden, string? reason)> Moderations { get; } = new();

    public Task ModerateMessageAsync(Guid conversationId, long seq, bool hidden, string? reason)
    {
        lock (_lock) Moderations.Add((conversationId, seq, hidden, reason));
        return Task.CompletedTask;
    }

    public Task<List<MessageEnvelope>> FetchEnvelopesAsync(
        Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        if (!Envelopes.TryGetValue(conversationId, out var list))
            return Task.FromResult(new List<MessageEnvelope>());

        IEnumerable<MessageEnvelope> q = list.OrderBy(e => e.Seq);
        if (afterSeq.HasValue) q = q.Where(e => e.Seq > afterSeq.Value);
        if (beforeSeq.HasValue) q = q.Where(e => e.Seq < beforeSeq.Value);
        return Task.FromResult(q.Take(limit).ToList());
    }

    public Task AddMemberAsync(Guid conversationId, Guid userId, bool includeHistory)
    {
        lock (_lock) Adds.Add((conversationId, userId, includeHistory));
        return Task.CompletedTask;
    }

    public Task<int> RemoveMemberAsync(Guid conversationId, Guid userId)
    {
        lock (_lock)
        {
            Removes.Add((conversationId, userId));
            // Mima il bump server-side: nuovo epoch = (epoch più alto noto per la conv) + 1.
            var highest = Keys.Keys.Where(k => k.conversationId == conversationId)
                .Select(k => k.epochId).DefaultIfEmpty(0).Max();
            return Task.FromResult(highest + 1);
        }
    }

    public List<(Guid conversationId, Guid userId, string role)> RoleChanges { get; } = new();
    public List<(Guid conversationId, string name)> Renames { get; } = new();
    public List<Guid> Deletes { get; } = new();

    public Task SetMemberRoleAsync(Guid conversationId, Guid userId, string role)
    {
        lock (_lock) RoleChanges.Add((conversationId, userId, role));
        return Task.CompletedTask;
    }

    public Task RenameConversationAsync(Guid conversationId, string newName)
    {
        lock (_lock) Renames.Add((conversationId, newName));
        return Task.CompletedTask;
    }

    public Task DeleteConversationAsync(Guid conversationId)
    {
        lock (_lock) Deletes.Add(conversationId);
        return Task.CompletedTask;
    }

    public Task<Guid> StartMigrationAsync(Guid conversationId, MigrationMode mode)
    {
        var jobId = Guid.NewGuid();
        lock (_lock) Migrations.Add((conversationId, mode, jobId));
        return Task.FromResult(jobId);
    }

    public Task CheckpointMigrationAsync(Guid conversationId, Guid jobId, int batchId, int progressPercent)
    {
        lock (_lock) Checkpoints.Add((conversationId, jobId, batchId, progressPercent));
        return Task.CompletedTask;
    }

    public Task FinalizeMigrationAsync(Guid conversationId, Guid jobId)
    {
        lock (_lock) Finalizes.Add((conversationId, jobId));
        return Task.CompletedTask;
    }

    public List<(Guid conversationId, Guid jobId)> Cancels { get; } = new();
    public List<(Guid conversationId, Guid jobId)> ForceFinalizes { get; } = new();

    public Task CancelMigrationAsync(Guid conversationId, Guid jobId)
    {
        lock (_lock) Cancels.Add((conversationId, jobId));
        return Task.CompletedTask;
    }

    public Task ForceFinalizeMigrationAsync(Guid conversationId, Guid jobId)
    {
        lock (_lock) ForceFinalizes.Add((conversationId, jobId));
        return Task.CompletedTask;
    }

    public Task ReplaceMessageAsync(Guid conversationId, long seq, MessageEnvelope envelope)
    {
        lock (_lock)
        {
            Replaced.Add((conversationId, seq, envelope));
            // Simula il vero PersistHandler: la riga al `seq` viene sostituita dall'envelope
            // ricevuto. Senza questo, CountEnvelopesBelowEpochAsync vede ancora la versione
            // pre-rewrite e la pre-check del custode reputa la saga incompleta.
            if (Envelopes.TryGetValue(conversationId, out var list))
            {
                var idx = list.FindIndex(e => e.Seq == seq);
                if (idx >= 0) list[idx] = envelope;
            }
        }
        return Task.CompletedTask;
    }

    public Task PostTombstonesAsync(Guid conversationId, IEnumerable<TombstoneRecord> tombstones)
    {
        lock (_lock) Tombstones.Add((conversationId, tombstones.ToList()));
        return Task.CompletedTask;
    }

    public Task<int> CountEnvelopesBelowEpochAsync(Guid conversationId, int epochBelow)
    {
        if (!Envelopes.TryGetValue(conversationId, out var list))
            return Task.FromResult(0);
        return Task.FromResult(list.Count(e => e.EpochId < epochBelow));
    }

    public Dictionary<Guid, List<ChainBoundary>> ChainBoundaries { get; } = new();

    public Task<List<ChainBoundary>> GetChainBoundariesAsync(Guid conversationId)
    {
        if (!ChainBoundaries.TryGetValue(conversationId, out var list))
            return Task.FromResult(new List<ChainBoundary>());
        return Task.FromResult(list.OrderBy(b => b.AfterSeq).ToList());
    }

    /// <summary>Directory device per la verifica firme: i test popolano questa lista (deviceId  SPKI).</summary>
    public List<DevicePublicKey> Devices { get; } = new();

    public Task<List<DevicePublicKey>> GetConversationDevicesAsync(Guid conversationId)
        => Task.FromResult(Devices.ToList());

    /// <summary>Device risolvibili per id (lookup storico) ma NON membri attivi della conversazione,
    /// es. un membro rimosso. GetDeviceAsync cerca qui + tra i device della conversazione.</summary>
    public List<DevicePublicKey> DirectoryDevices { get; } = new();

    public Task<DevicePublicKey?> GetConversationSenderDeviceAsync(Guid conversationId, Guid deviceId)
        => Task.FromResult(Devices.Concat(DirectoryDevices).FirstOrDefault(d => d.DeviceId == deviceId));

    /// <summary>Wrap depositati dal client (provisioning/grant/rotation). I test possono ispezionarli.</summary>
    public List<(Guid conversationId, List<WrappedKey> wraps)> PostedKeys { get; } = new();

    public Task PostKeysAsync(Guid conversationId, List<WrappedKey> wraps)
    {
        lock (_lock)
        {
            PostedKeys.Add((conversationId, wraps));
            // Riflette i wrap nel dizionario Keys così GetKeysAsync li restituisce (per il device "self").
            foreach (var w in wraps)
                if (w.DeviceId == CurrentUserId)
                    Keys[(conversationId, w.EpochId)] = w.WrappedCek;
        }
        return Task.CompletedTask;
    }

    public List<DeviceRegistration> RegisteredDevices { get; } = new();

    public Task RegisterDeviceAsync(DeviceRegistration registration)
    {
        lock (_lock) RegisteredDevices.Add(registration);
        return Task.CompletedTask;
    }
}
