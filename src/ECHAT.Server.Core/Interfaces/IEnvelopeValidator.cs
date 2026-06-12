using ECHAT.Models.Domain;

namespace ECHAT.Server.Core.Interfaces;

public interface IEnvelopeValidator
{
    /// <summary>
    /// Verifica le dimensioni di un <see cref="MessageEnvelope"/> in arrivo.
    /// Ritorna il motivo in caso di errore, null se entro i limiti.
    /// </summary>
    string? Validate(MessageEnvelope envelope);
}
