using ECHAT.Server.Core.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ECHAT.Server.App.Middleware;

/// <summary>
/// Mappa le exception dei service di Core sui codici HTTP corrispondenti. Tiene i controller
/// sottili (orchestrazione + claim parsing) senza catene di try/catch.
/// </summary>
public class CoreExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case NotFoundException nf:
                context.Result = new NotFoundObjectResult(new { error = nf.Message });
                context.ExceptionHandled = true;
                break;
            case ForbiddenException:
                context.Result = new ForbidResult();
                context.ExceptionHandled = true;
                break;
            case ValidationException v:
                context.Result = new BadRequestObjectResult(new { error = v.Message });
                context.ExceptionHandled = true;
                break;
            case ConflictException c:
                context.Result = new ConflictObjectResult(new { error = c.Message });
                context.ExceptionHandled = true;
                break;
        }
    }
}
