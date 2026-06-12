using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Pipeline;

public class IngestContext
{
    public MessageEnvelope Envelope { get; }
    public Guid UserId { get; set; }
    public MessageAck? Ack { get; set; }
    public bool IsDuplicate { get; set; }

    public IngestContext(MessageEnvelope envelope)
    {
        Envelope = envelope;
    }
}
