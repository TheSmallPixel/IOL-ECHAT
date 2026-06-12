using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// Fake del tracker: registra BeginLocal/EndLocal e gli Update, così i test verificano che la
/// migrazione FullReencrypt apra lo scope locale e inoltri il progress.
/// </summary>
public class FakeMigrationStateTracker : IMigrationStateTracker
{
    public List<Guid> BeginLocalCalls { get; } = new();
    public int EndLocalCount { get; private set; }
    public List<(Guid conversationId, MigrationProgress progress)> Updates { get; } = new();

    public event Action<Guid>? StateChanged;

    public MigrationProgress? Get(Guid conversationId) => null;
    public bool IsActive(Guid conversationId) => false;

    public IDisposable BeginLocal(Guid conversationId)
    {
        BeginLocalCalls.Add(conversationId);
        return new Scope(this);
    }

    public void Update(Guid conversationId, MigrationProgress progress)
    {
        Updates.Add((conversationId, progress));
        StateChanged?.Invoke(conversationId);
    }

    private sealed class Scope : IDisposable
    {
        private readonly FakeMigrationStateTracker _t;
        public Scope(FakeMigrationStateTracker t) => _t = t;
        public void Dispose() => _t.EndLocalCount++;
    }
}
