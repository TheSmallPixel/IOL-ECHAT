namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Astrazione sul ritardo casuale "simula digitazione" usata da
/// <see cref="IAiAutoReplyOrchestrator"/>. Iniettabile per rendere i test deterministici.
/// </summary>
public interface IDelayProvider
{
    /// <summary>
    /// Attende un ritardo casuale tra <paramref name="minMs"/> (incluso) e <paramref name="maxMs"/>
    /// (escluso) millisecondi, osservando la cancellazione.
    /// </summary>
    Task DelayRandomAsync(int minMs, int maxMs, CancellationToken ct);
}
