using ECHAT.Models.Domain;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using Microsoft.Extensions.Logging;

namespace ECHAT.Server.Core.Pipeline;

/// <summary>
/// Difesa-in-profondità server-side per S3: verifica la firma ECDSA P-256 dell'envelope contro la
/// chiave pubblica del device mittente (dalla directory). Il client verifica già le firme in ricezione,
/// ma il server rifiuta comunque l'ingest di un envelope con firma invalida: così un envelope forgiato
/// non entra mai nello store. Va PRIMA di <see cref="DeduplicationHandler"/> (ogni envelope è verificato
/// crittograficamente prima di qualunque altro effetto/dedup) e PRIMA di <c>PersistHandler</c>.
/// La firma è sul digest di <see cref="EnvelopeHasher"/>, che lega conv/msg/seq/epoch/senderDeviceId+ciphertext.
/// </summary>
public class SignatureVerificationHandler : IIngestHandler
{
    private readonly IDevicePublicKeyStore _devices;
    private readonly ILogger<SignatureVerificationHandler> _logger;

    public SignatureVerificationHandler(IDevicePublicKeyStore devices, ILogger<SignatureVerificationHandler> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    public async Task HandleAsync(IngestContext context, Func<Task> next)
    {
        var env = context.Envelope;

        var device = await _devices.GetActiveByDeviceAsync(env.SenderDeviceId);
        if (device is null)
            throw new ForbiddenException(); // SenderIdentityHandler dovrebbe già averlo escluso

        var hash = EnvelopeHasher.Compute(env);
        if (!EcdsaVerifier.VerifyP1363(device.EcdsaSpki, hash, env.Signature))
        {
            _logger.LogWarning(
                "Ingest rejected (invalid signature): userId={UserId} deviceId={DeviceId} conversation={ConversationId} seq={Seq}",
                context.UserId, env.SenderDeviceId, env.ConversationId, env.Seq);
            throw new ForbiddenException();
        }

        await next();
    }
}
