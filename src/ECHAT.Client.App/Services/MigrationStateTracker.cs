using System.Collections.Concurrent;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Events;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Implementazione di <see cref="IMigrationStateTracker"/>: si sottoscrive a
/// <see cref="IRealtimeClient.OnJobProgress"/> per ricevere lo stato delle migrazioni guidate
/// da altri device, e accetta update locali da chi guida la saga su questo device.
/// </summary>
public sealed class MigrationStateTracker : IMigrationStateTracker, IDisposable
{
    /// <summary>Quanto a lungo lo stato terminale (Completed/Cancelled/Failed) resta visibile
    /// nel banner prima di sparire, così l'utente vede l'esito.</summary>
    private static readonly TimeSpan TerminalLinger = TimeSpan.FromSeconds(2.5);

    private readonly IRealtimeClient _realtime;
    private readonly IMigrationStateManager _manager;
    private readonly ConcurrentDictionary<Guid, MigrationProgress> _state = new();
    private readonly HashSet<Guid> _locallyDriven = new();
    private readonly object _localLock = new();

    public MigrationStateTracker(IRealtimeClient realtime, IMigrationStateManager manager)
    {
        _realtime = realtime;
        _manager = manager;
        _realtime.OnJobProgress += OnRemoteProgress;
    }

    public event Action<Guid>? StateChanged;

    public MigrationProgress? Get(Guid conversationId)
        => _state.TryGetValue(conversationId, out var s) ? s : null;

    public bool IsActive(Guid conversationId)
        => _state.TryGetValue(conversationId, out var s) && !_manager.IsTerminal(s.Phase);

    public IDisposable BeginLocal(Guid conversationId)
    {
        lock (_localLock) _locallyDriven.Add(conversationId);
        Update(conversationId, new MigrationProgress(MigrationPhase.Starting));
        return new LocalScope(this, conversationId);
    }

    public void Update(Guid conversationId, MigrationProgress progress)
    {
        _state[conversationId] = progress;
        StateChanged?.Invoke(conversationId);

        if (_manager.IsTerminal(progress.Phase))
        {
            // Lascia visibile lo stato terminale per qualche secondo così l'utente vede
            // chiaramente Completed/Cancelled/Failed prima che il banner sparisca. Confronto
            // per equality (record value) per non cancellare se nel frattempo è partita una
            // nuova migrazione sulla stessa conversazione.
            _ = ScheduleClearAsync(conversationId, progress);
        }
    }

    private async Task ScheduleClearAsync(Guid conversationId, MigrationProgress terminalSnapshot)
    {
        try
        {
            await Task.Delay(TerminalLinger);
            var current = _state.TryGetValue(conversationId, out var s) ? s : null;
            if (_manager.ShouldClearTerminal(current, terminalSnapshot))
            {
                if (_state.TryRemove(conversationId, out _))
                    StateChanged?.Invoke(conversationId);
            }
        }
        catch (TaskCanceledException)
        {
            // se l'app va in dispose il delay si interrompe: niente da fare
        }
    }

    private void OnRemoteProgress(JobProgressEvent evt)
    {
        // Se siamo noi a guidare la saga, il flusso IProgress locale è più preciso e
        // gestisce già Starting/Reencrypting/Finalizing/Completed/Failed: ignoriamo l'eco
        // SignalR per evitare update fuori sequenza.
        bool isLocal;
        lock (_localLock) isLocal = _locallyDriven.Contains(evt.ConversationId);
        if (_manager.ShouldIgnoreRemote(isLocal)) return;

        // Per saghe altrui mappiamo lo Status del job a una fase. ProgressPercent va in Processed
        // come "0..100"; la UI distingue tra fonte locale (count) e remota (percent) leggendo
        // Total: null = i numeri sono percentuali, non assoluti.
        Update(evt.ConversationId, _manager.BuildRemoteProgress(evt));
    }

    private void EndLocal(Guid conversationId)
    {
        lock (_localLock) _locallyDriven.Remove(conversationId);

        // Se l'ultimo update ha già messo lo stato in una fase terminale, lascia che il
        // timer di ScheduleClearAsync lo pulisca così l'utente vede Completed/Cancelled/Failed.
        // Pulisce subito solo se per qualche motivo non è arrivato un report terminale
        // (situazione anomala: RunStrongRevokeAsync ne emette uno per ogni path).
        if (_state.TryGetValue(conversationId, out var s) && _manager.IsTerminal(s.Phase))
            return;

        if (_state.TryRemove(conversationId, out _))
            StateChanged?.Invoke(conversationId);
    }

    public void Dispose() => _realtime.OnJobProgress -= OnRemoteProgress;

    private sealed class LocalScope : IDisposable
    {
        private readonly MigrationStateTracker _tracker;
        private readonly Guid _conv;
        private int _disposed;

        public LocalScope(MigrationStateTracker tracker, Guid conv) { _tracker = tracker; _conv = conv; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _tracker.EndLocal(_conv);
        }
    }
}
