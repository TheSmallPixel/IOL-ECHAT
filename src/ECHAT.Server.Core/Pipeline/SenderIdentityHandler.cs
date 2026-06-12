using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ECHAT.Server.Core.Pipeline;

/// <summary>
/// PRIMO handler della pipeline di ingest: chiude S4 (sender spoofing). Lega l'identità dichiarata
/// nell'envelope al principal autenticato (JWT) e alla directory dei device:
///   1) <c>envelope.SenderUserId</c> deve coincidere con <c>context.UserId</c> (preso dal JWT);
///   2) <c>envelope.SenderDeviceId</c> deve essere un device ATTIVO registrato per quello stesso utente.
/// Un client non può quindi spacciarsi per un altro utente o per un device non suo. La firma ECDSA
/// (verificata in <see cref="SignatureVerificationHandler"/>) è la difesa indipendente che impedisce
/// di falsificare il contenuto anche conoscendo gli id.
/// </summary>
public class SenderIdentityHandler : IIngestHandler
{
    private readonly IDevicePublicKeyStore _devices;
    private readonly ILogger<SenderIdentityHandler> _logger;

    public SenderIdentityHandler(IDevicePublicKeyStore devices, ILogger<SenderIdentityHandler> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    public async Task HandleAsync(IngestContext context, Func<Task> next)
    {
        var env = context.Envelope;

        if (env.SenderUserId != context.UserId)
        {
            _logger.LogWarning(
                "Ingest rejected (sender user mismatch): authenticated={UserId} claimed={ClaimedUserId} conversation={ConversationId} seq={Seq}",
                context.UserId, env.SenderUserId, env.ConversationId, env.Seq);
            throw new ForbiddenException();
        }

        var device = await _devices.GetActiveByDeviceAsync(env.SenderDeviceId);
        if (device is null || device.UserId != context.UserId)
        {
            _logger.LogWarning(
                "Ingest rejected (device not owned by sender): userId={UserId} deviceId={DeviceId} conversation={ConversationId} seq={Seq}",
                context.UserId, env.SenderDeviceId, env.ConversationId, env.Seq);
            throw new ForbiddenException();
        }

        await next();
    }
}
