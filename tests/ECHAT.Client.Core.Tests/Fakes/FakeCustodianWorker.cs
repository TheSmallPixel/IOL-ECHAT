using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// Fake del custode: registra le invocazioni di RunStrongRevoke e ri-emette il progress fornito
/// (Starting -> Reencrypting -> Completed) così i test verificano l'aggregazione degli update.
/// </summary>
public class FakeCustodianWorker : ICustodianWorker
{
    public List<(Guid conversationId, MigrationMode mode)> StrongRevokes { get; } = new();
    public List<(Guid conversationId, Guid jobId)> ForceFinalizes { get; } = new();

    public Task RewrapKeysForMemberAsync(Guid conversationId, Guid newMemberDeviceId) => Task.CompletedTask;
    public Task GenerateGapTombstonesAsync(Guid conversationId, long fromSeq, long toSeq) => Task.CompletedTask;

    public Task RunStrongRevokeAsync(
        Guid conversationId, MigrationMode mode, CancellationToken ct, IProgress<MigrationProgress>? progress = null)
    {
        StrongRevokes.Add((conversationId, mode));
        progress?.Report(new MigrationProgress(MigrationPhase.Starting));
        progress?.Report(new MigrationProgress(MigrationPhase.Reencrypting, 1, 2));
        progress?.Report(new MigrationProgress(MigrationPhase.Completed, 2, 2));
        return Task.CompletedTask;
    }

    public Task ForceFinalizeAsync(Guid conversationId, Guid jobId)
    {
        ForceFinalizes.Add((conversationId, jobId));
        return Task.CompletedTask;
    }
}
