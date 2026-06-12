using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Interfaces;

public interface ISequenceService
{
    Task<SeqReservation> ReserveRangeAsync(Guid conversationId, Guid deviceId, int count);
    Task<bool> ValidateLeaseAsync(Guid conversationId, long seq, string leaseToken);
}
