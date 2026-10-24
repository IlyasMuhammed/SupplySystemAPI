using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 API-CONTRACT "Errors" — turns a <see cref="IJournalCodedError"/> into the coded body
/// <c>{ success: false, message, result: { errorCode, errors: [{ errorCode, message, lineNumber? }] } }</c>, 400 for a
/// validation error and 409 for a state conflict. Put it on every A47 controller (journal entries, journal types,
/// reconciliation, trial balance, period close); other exceptions go on to the global middleware.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class JournalErrorFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not IJournalCodedError coded) return;
        var status = context.Exception is JournalConflictException ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
        context.Result = new ObjectResult(ApiResponse.Fail(context.Exception.Message, new
        {
            errorCode = coded.ErrorCode,
            errors = coded.Errors.Select(e => new { errorCode = e.ErrorCode, message = e.Message, lineNumber = e.LineNumber }).ToList(),
        })) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
