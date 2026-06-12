namespace ECHAT.Server.Core.Pipeline;

public interface IIngestHandler
{
    Task HandleAsync(IngestContext context, Func<Task> next);
}
