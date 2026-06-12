using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Accesso alle CEK per conversazione (E2EE redesign, S1). Il server NON genera, copia né deriva
/// più alcuna CEK: si limita a servire i wrap che i client hanno depositato (<see cref="ResolveKeysAsync"/>)
/// e a conservare quelli che i client depositano (<see cref="StoreClientWrapsAsync"/>). Ogni
/// <c>WrappedCek</c> è una CEK cifrata con la chiave pubblica RSA del device destinatario: il server
/// non vede mai la CEK in chiaro. Il controllo di proprietà del device resta per impedire a un utente
/// di leggere i wrap di un altro.
/// </summary>
public class KeyAccessService
{
    // RSA-OAEP-2048 wrappa una CEK da 32 byte producendo ESATTAMENTE 256 byte; +1 byte magic = 257.
    private const int ExpectedWrapLength = 257;
    private const byte MagicRsaWrapV1 = 0xB2;
    private const byte SupportedKeyWrapVersion = 1; // RSA-OAEP v1

    private readonly IKeyEnvelopeStore _keyStore;
    private readonly IMemberStore _members;
    private readonly ILogger<KeyAccessService> _logger;

    public KeyAccessService(IKeyEnvelopeStore keyStore, IMemberStore members, ILogger<KeyAccessService> logger)
    {
        _keyStore = keyStore;
        _members = members;
        _logger = logger;
    }

    /// <summary>
    /// Un membro può leggere solo i propri wrap: il device richiesto deve coincidere con l'utente.
    /// (Modello attuale: una riga per (conversation, epoch, deviceId=userId); il wrap è cifrato per
    /// il device attivo dell'utente.)
    /// </summary>
    public bool ValidateDeviceOwnership(Guid userId, Guid requestedDevice) => requestedDevice == userId;

    /// <summary>
    /// Risolve i wrap della CEK per l'utente richiedente. Niente più self-heal/backfill: se non ci
    /// sono wrap, la lista è vuota (la conversazione non è stata ancora provisionata dal client, o
    /// l'utente non ha ancora ricevuto un grant). Il chiamante ha già verificato auth + ownership.
    /// </summary>
    public Task<List<WrappedKey>> ResolveKeysAsync(
        Guid conversationId,
        Guid userId,
        int? epochId,
        Guid? deviceId)
    {
        var requestedDevice = deviceId ?? userId;
        return _keyStore.GetKeysAsync(conversationId, epochId, requestedDevice);
    }

    /// <summary>
    /// Deposita i wrap forniti dal client (E2EE): create/grant/rotation. Il client che possiede la CEK
    /// la ri-wrappa con la chiave pubblica RSA di ogni device destinatario e posta i blob qui. Il server
    /// li conserva senza poterli leggere. Autorizzazione (membro della conversazione) imposta dal filtro
    /// sul controller. Stampiamo <c>ConversationId</c> dalla route per non fidarci del body.
    /// </summary>
    public async Task StoreClientWrapsAsync(Guid conversationId, IReadOnlyList<WrappedKey> wraps)
    {
        if (wraps is null || wraps.Count == 0)
            throw new ValidationException("No key wraps provided.");

        // I destinatari legittimi di un wrap sono SOLO i membri attivi della conversazione (nel modello
        // attuale DeviceId == userId). Questo impedisce a chi distribuisce le chiavi di scrivere/poison-
        // are wrap per device non-membri o inesistenti. L'upsert (conv,epoch,device) lato repo significa
        // che un wrap valido sostituisce il precedente: limitare i target ai membri + validare la forma
        // del blob riduce la superficie di DoS (la POST è inoltre ristretta agli admin dal controller).
        var activeMembers = (await _members.ListActiveWithUserAsync(conversationId))
            .Select(m => m.UserId)
            .ToHashSet();

        var stamped = new List<WrappedKey>(wraps.Count);
        foreach (var w in wraps)
        {
            if (w.EpochId <= 0)
                throw new ValidationException($"Invalid epoch {w.EpochId} in key wrap.");
            if (w.DeviceId == Guid.Empty)
                throw new ValidationException("Key wrap missing target device.");
            if (!activeMembers.Contains(w.DeviceId))
                throw new ForbiddenException(); // target non è un membro attivo  niente cross-poisoning
            if (w.KeyWrapVersion != SupportedKeyWrapVersion)
                throw new ValidationException(
                    $"Unsupported key wrap version {w.KeyWrapVersion} (expected {SupportedKeyWrapVersion}).");
            if (w.WrappedCek is null || w.WrappedCek.Length != ExpectedWrapLength || w.WrappedCek[0] != MagicRsaWrapV1)
                throw new ValidationException(
                    $"Malformed wrapped CEK (len={w.WrappedCek?.Length ?? 0}, expected {ExpectedWrapLength} with magic 0x{MagicRsaWrapV1:X2}).");

            stamped.Add(new WrappedKey
            {
                ConversationId = conversationId, // dalla route, non dal body
                EpochId = w.EpochId,
                DeviceId = w.DeviceId,
                WrappedCek = w.WrappedCek,
                KeyWrapVersion = w.KeyWrapVersion
            });
        }

        await _keyStore.StoreWrapsAsync(conversationId, stamped);
        _logger.LogInformation(
            "Stored {Count} client key wrap(s) for conversation={ConversationId} epochs=[{Epochs}]",
            stamped.Count, conversationId, string.Join(",", stamped.Select(s => s.EpochId).Distinct()));
    }
}
