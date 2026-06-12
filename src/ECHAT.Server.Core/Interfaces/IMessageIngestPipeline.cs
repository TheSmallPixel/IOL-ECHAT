using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Interfaces;

public interface IMessageIngestPipeline
{
    /// <summary><paramref name="userId"/> è il principal autenticato (JWT): il SenderIdentityHandler
    /// lo confronta con l'identità dichiarata nell'envelope (S4).</summary>
    Task<MessageAck> IngestAsync(MessageEnvelope envelope, Guid userId);
}
