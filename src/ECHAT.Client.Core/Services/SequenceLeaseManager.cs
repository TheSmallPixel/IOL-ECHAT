using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Services;

public class SequenceLeaseManager : ISequenceLeaseManager
{
    private readonly Dictionary<Guid, LeaseState> _leases = new();
    private readonly object _lock = new();

    public void ApplyReservation(Guid conversationId, SeqReservation reservation)
    {
        lock (_lock)
        {
            _leases[conversationId] = new LeaseState
            {
                NextSeq = reservation.StartSeq,
                EndSeq = reservation.EndSeq,
                AnchorEnvelopeHash = reservation.AnchorEnvelopeHash,
                LeaseToken = reservation.LeaseToken
            };
        }
    }

    public string GetLeaseToken(Guid conversationId)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(conversationId, out var state) ? state.LeaseToken : string.Empty;
        }
    }

    public Task<long> GetNextSeqAsync(Guid conversationId)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(conversationId, out var state))
                throw new InvalidOperationException($"No lease available for conversation {conversationId}");

            if (state.NextSeq > state.EndSeq)
                throw new InvalidOperationException($"Lease exhausted for conversation {conversationId}");

            var seq = state.NextSeq;
            state.NextSeq++;
            return Task.FromResult(seq);
        }
    }

    public Task<byte[]> GetAnchorHashAsync(Guid conversationId)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(conversationId, out var state))
                throw new InvalidOperationException($"No lease available for conversation {conversationId}");

            return Task.FromResult(state.AnchorEnvelopeHash);
        }
    }

    public bool HasAvailableSeq(Guid conversationId)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(conversationId, out var state) && state.NextSeq <= state.EndSeq;
        }
    }

    private class LeaseState
    {
        public long NextSeq { get; set; }
        public long EndSeq { get; set; }
        public byte[] AnchorEnvelopeHash { get; set; } = Array.Empty<byte>();
        public string LeaseToken { get; set; } = string.Empty;
    }
}
