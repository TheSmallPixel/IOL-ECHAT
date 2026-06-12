using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Services;

/// <summary>Ritardo casuale reale basato su <see cref="Random.Shared"/> e <see cref="Task.Delay(int, CancellationToken)"/>.</summary>
public sealed class RandomDelayProvider : IDelayProvider
{
    public Task DelayRandomAsync(int minMs, int maxMs, CancellationToken ct)
        => Task.Delay(Random.Shared.Next(minMs, maxMs), ct);
}
