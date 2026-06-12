namespace ECHAT.Models.Dtos;

/// <summary>
/// Marca un seq dopo il quale il chain hash della conversazione è stato legittimamente
/// "ricostruito" da una saga FullReencrypt. Il client validator usa questi record per non
/// segnalare come "Chain: Break" l'envelope subito dopo (il suo PrevEnvelopeHash punta
/// all'hash ORIGINALE di un envelope sostituito dal custode).
/// </summary>
public record ChainBoundary(long AfterSeq, int AtEpoch, DateTime CreatedAt);
