namespace ECHAT.Server.Core.Interfaces;

/// <summary>
/// Specifica di un singolo gap-tombstone da iniettare nella catena seq.
/// </summary>
public class TombstoneSpec
{
    public Guid MessageId { get; set; }
    public long Seq { get; set; }
    public int EpochId { get; set; }
    public byte[]? Nonce { get; set; }
    public byte[]? Ciphertext { get; set; }
    public byte[]? Signature { get; set; }
}

/// <summary>
/// Corpo della richiesta di iniezione tombstone (bind dal controller).
/// </summary>
public class InjectTombstoneRequest
{
    public List<TombstoneSpec> Tombstones { get; set; } = new();
}

/// <summary>
/// Esito dell'iniezione dei tombstone. <see cref="Error"/> non null indica una validazione fallita
/// (il controller mappa su BadRequest); in caso di successo i campi popolano audit + notifica.
/// </summary>
public class TombstoneInjectionResult
{
    public string? Error { get; init; }
    public int Count { get; init; }
    public long FromSeq { get; init; }
    public long ToSeq { get; init; }
    public long AnchorSeq { get; init; }
    public Guid LastMessageId { get; init; }
    public int LastEpochId { get; init; }

    public bool Succeeded => Error is null;

    public static TombstoneInjectionResult Failed(string error) => new() { Error = error };
}

/// <summary>
/// Inietta gap-tombstone nella catena seq globale di una conversazione: valida la lista,
/// costruisce gli envelope, li persiste con hash concatenati e aggiorna l'anchor. Il controller
/// mantiene route/[Authorize]/policy/estrazione claim e i side-effect (audit + notifier) guidati
/// dal risultato.
/// </summary>
public interface ITombstoneInjectionService
{
    Task<TombstoneInjectionResult> InjectTombstonesAsync(Guid conversationId, Guid senderUserId, List<TombstoneSpec> specs);
}
