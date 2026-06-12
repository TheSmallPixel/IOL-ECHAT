namespace ECHAT.Server.App.Data.Entities;

/// <summary>
/// Marca un punto in cui la catena di hash della conversazione è stata legittimamente
/// "ricostruita" da una saga FullReencrypt. Il client validator usa questi record per evitare
/// di segnalare come "Chain: Break" l'envelope immediatamente successivo a una riscrittura
/// massiva: il suo <c>PrevEnvelopeHash</c> punta all'hash ORIGINALE di un envelope che il
/// custode ha sostituito (e quindi ha un hash diverso ora).
///
/// AfterSeq = ultimo seq sostituito dal custode (= MigrationJobEntity.MaxReplacedSeq).
/// AtEpoch = epoch di target del FullReencrypt (= epoch corrente al momento del finalize).
/// </summary>
public class ChainBoundaryEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long AfterSeq { get; set; }
    public int AtEpoch { get; set; }
    public DateTime CreatedAt { get; set; }
}
