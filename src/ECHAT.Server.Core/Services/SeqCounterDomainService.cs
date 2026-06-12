using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Logica di dominio (stateless, senza I/O) per il contatore di sequenza:
/// algoritmo di self-heal della riserva di range e guardia di aggiornamento dell'anchor.
/// Il repository EF mantiene fetch/create + mapping + SaveChanges e delega qui le decisioni.
/// </summary>
public class SeqCounterDomainService
{
    /// <summary>
    /// Calcola il range contiguo da riservare a partire dallo stato corrente del contatore.
    /// Self-heal: se <c>NextSeq</c> è rimasto indietro rispetto all'anchor o ai messaggi già
    /// persistiti, salta avanti per non emettere un seq già usato. Ritorna il blocco riservato,
    /// l'anchor corrente e il nuovo valore di <c>NextSeq</c> da persistere.
    /// </summary>
    public SeqRangeReservationResult ReserveRange(SeqCounter current, int count, long maxMessageSeq)
    {
        // Self-heal: se NextSeq è rimasto indietro rispetto a anchor o ai messaggi già persistiti,
        // saltiamo avanti per non emettere un seq già usato.
        var minStart = current.AnchorSeq + 1;
        if (current.AnchorSeq == 0)
            minStart = Math.Max(minStart, maxMessageSeq + 1);

        var nextSeq = current.NextSeq;
        if (nextSeq < minStart) nextSeq = minStart;

        var startSeq = nextSeq;
        var endSeq = startSeq + count - 1;

        return new SeqRangeReservationResult
        {
            Reservation = new SeqRangeReservation
            {
                StartSeq = startSeq,
                EndSeq = endSeq,
                AnchorSeq = current.AnchorSeq,
                AnchorEnvelopeHash = current.AnchorEnvelopeHash
            },
            NewNextSeq = endSeq + 1
        };
    }

    /// <summary>
    /// L'anchor può avanzare solo verso seq strettamente maggiori del valore corrente.
    /// </summary>
    public bool CanUpdateAnchor(SeqCounter current, long proposedAnchorSeq)
        => proposedAnchorSeq > current.AnchorSeq;
}

/// <summary>
/// Esito del calcolo di <see cref="SeqCounterDomainService.ReserveRange"/>:
/// blocco riservato + nuovo valore di NextSeq da persistere.
/// </summary>
public class SeqRangeReservationResult
{
    public SeqRangeReservation Reservation { get; init; } = new();
    public long NewNextSeq { get; init; }
}
