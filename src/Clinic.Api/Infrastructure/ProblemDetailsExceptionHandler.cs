using Clinic.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Infrastructure;

/// <summary>FR-API-04: every error leaves the API as RFC 7807 ProblemDetails.</summary>
public sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ProblemDetailsExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem = exception switch
        {
            RequestValidationException v => new ValidationProblemDetails(v.Errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = v.Message,
            },
            NotFoundException e => new() { Status = StatusCodes.Status404NotFound, Title = "Not found", Detail = e.Message },
            ConflictException e => new() { Status = StatusCodes.Status409Conflict, Title = "Conflict", Detail = e.Message },
            ForbiddenException e => new() { Status = StatusCodes.Status403Forbidden, Title = "Forbidden", Detail = e.Message },
            AuthFailedException e => new()
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Authentication failed",
                Detail = e.Message,
                Type = e.ProblemType,
            },
            Application.Sync.UpdateRequiredException e => new()
            {
                Status = StatusCodes.Status426UpgradeRequired,
                Title = "Update required",
                Detail = e.Message,
                Type = Shared.Sync.SyncRoutes.UpdateRequiredProblem,
                Extensions = { ["minAppVersion"] = e.MinVersion },
            },
            DbUpdateException => new()
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "The change conflicts with existing data. Refresh and try again.",
            },
            _ => new()
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                // Never in production: stack traces only help attackers.
                Detail = environment.IsDevelopment() || environment.IsEnvironment("Testing") ? exception.ToString() : null,
            },
        };

        if (problem.Status >= 500 || exception is DbUpdateException)
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);

        http.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
