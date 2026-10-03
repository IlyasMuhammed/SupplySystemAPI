using Microsoft.EntityFrameworkCore;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using System.Net;
using System.Text.Json;

namespace SMS.API.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception (reference {TraceIdentifier}): {Message}", context.TraceIdentifier, ex.Message);
            await HandleExceptionAsync(context, ex);
        }
    }

    /// <summary>
    /// The error body. Only this application's own exceptions — whose messages are written for the person using
    /// the app — carry their text in <see cref="ErrorDetail"/>. Anything else (a database, file-system or framework
    /// failure) says only that something went wrong, with a reference to the full error in the server log: its
    /// text names server paths, databases, tables, columns and values, and this body reaches anonymous callers
    /// too (the supplier portal, sign-in).
    /// </summary>
    private static Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, message, ownMessage) = exception switch
        {
            NotFoundException ex             => (HttpStatusCode.NotFound, ex.Message, true),
            AccountLockedException ex        => ((HttpStatusCode)429, ex.Message, true),
            ConflictException ex             => (HttpStatusCode.Conflict, ex.Message, true),
            BadRequestException ex           => (HttpStatusCode.BadRequest, ex.Message, true),
            ForbiddenException ex            => (HttpStatusCode.Forbidden, ex.Message, true),
            UnauthorizedException ex         => (HttpStatusCode.Unauthorized, ex.Message, true),
            UnprocessableEntityException ex  => ((HttpStatusCode)422, ex.Message, true),
            WorkflowNotFoundException ex     => (HttpStatusCode.NotFound, ex.Message, true),
            WorkflowConfigurationException ex => ((HttpStatusCode)422, ex.Message, true),
            ApproverResolutionException ex   => ((HttpStatusCode)422, ex.Message, true),
            // A stock counter was changed by someone else between this request's read and its
            // write. Nothing was saved; the client re-reads and retries.
            DbUpdateConcurrencyException     => (HttpStatusCode.Conflict,
                "Another user or process changed the same stock at the same time, so nothing was saved. Please try again.", false),
            _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred.", false)
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        // The log line written in InvokeAsync carries the same trace identifier.
        if (statusCode == HttpStatusCode.InternalServerError)
            message = $"{message} Reference: {context.TraceIdentifier}.";

        var response = ownMessage
            ? ApiResponse.Fail(message, new ErrorDetail
              {
                  ExceptionMessage = exception.Message,
                  ExceptionMessageDetail = exception.InnerException?.Message
              })
            : ApiResponse.Fail(message);

        return context.Response.WriteAsync(
            JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
