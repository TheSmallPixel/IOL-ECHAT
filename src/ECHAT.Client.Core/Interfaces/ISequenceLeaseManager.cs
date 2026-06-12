using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Interfaces;

public interface ISequenceLeaseManager
{
    void ApplyReservation(Guid conversationId, SeqReservation reservation);
    Task<long> GetNextSeqAsync(Guid conversationId);
    Task<byte[]> GetAnchorHashAsync(Guid conversationId);
    /// <summary>Restituisce il token del lease attivo, oppure stringa vuota se non c'è.</summary>
    string GetLeaseToken(Guid conversationId);
    bool HasAvailableSeq(Guid conversationId);
}
