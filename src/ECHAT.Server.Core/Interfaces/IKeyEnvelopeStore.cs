using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Interfaces;

public interface IKeyEnvelopeStore
{
    Task<List<WrappedKey>> GetKeysAsync(Guid conversationId, int? epochId, Guid? deviceId);
    Task StoreWrapsAsync(Guid conversationId, List<WrappedKey> wraps);
    Task DeleteWrapsAsync(Guid conversationId, int epochId, Guid? deviceId);

    /// <summary>
    /// Cancella TUTTI i wrap (ogni epoch) destinati ai device indicati in una conversazione.
    /// Usata al RemoveMember per crypto-shred-are le copie del rimosso: difesa in profondità
    /// oltre al check di membership di KeyAccessService.
    /// </summary>
    Task DeleteWrapsForDevicesAsync(Guid conversationId, IReadOnlyCollection<Guid> deviceIds);
}
