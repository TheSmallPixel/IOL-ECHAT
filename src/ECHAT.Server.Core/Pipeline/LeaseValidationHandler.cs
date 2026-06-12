using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Pipeline;

public class LeaseValidationHandler : IIngestHandler
{
    private readonly ISequenceService _sequenceService;

    public LeaseValidationHandler(ISequenceService sequenceService)
    {
        _sequenceService = sequenceService;
    }

    public async Task HandleAsync(IngestContext context, Func<Task> next)
    {
        var valid = await _sequenceService.ValidateLeaseAsync(
            context.Envelope.ConversationId,
            context.Envelope.Seq,
            context.Envelope.LeaseToken);

        if (!valid)
            throw new InvalidOperationException($"Invalid lease for seq {context.Envelope.Seq}");

        await next();
    }
}
